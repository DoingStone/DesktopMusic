# Verifies that the settings pane toggle's click plate is a CIRCLE, not a rectangle.
#
# The user's reference draws this button (the sidebar collapse toggle in the nav pane
# header) as a disc, so its hover fill must be round. A circle is only round if the button
# box is SQUARE and the corner radius is half the side - a wide box with the same radius
# would be a stadium, and no radius at all is the old stray rectangle.
#
# Requirements: the app must already be running with its settings window open, e.g.
#   Start-Process <exe> -ArgumentList '--settings'
# The script moves the mouse and raises the settings window for its screenshots, so it is
# not for unattended use.
#
# Usage:  & tools\verify-toggle-disc.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class ToggleProbe
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern int GetDpiForSystem();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static IntPtr FindSettingsWindow(uint pid, string excludeTitle)
    {
        IntPtr best = IntPtr.Zero;
        int bestScore = -1;
        EnumWindows((h, l) =>
        {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner != pid || !IsWindowVisible(h)) return true;

            var title = new StringBuilder(256);
            GetWindowText(h, title, 256);
            if (title.ToString() == excludeTitle) return true;   // that one is the overlay

            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            if (!cls.ToString().StartsWith("HwndWrapper")) return true;

            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            int w = Math.Abs(r.Right - r.Left), ht = Math.Abs(r.Bottom - r.Top);

            // A minimised window parks at the -32000 sentinel, which the DPI scale divides
            // down to about -25600; it is still the window to measure once restored.
            bool minimised = r.Left <= -20000 || r.Top <= -20000;
            if (!minimised && (w < 120 || ht < 80)) return true;

            int score = minimised ? 1 : w * ht;
            if (score > bestScore) { bestScore = score; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
'@

if (-not [ToggleProbe]::SetProcessDPIAware()) { Write-Host 'WARN  SetProcessDPIAware returned false' }
$scale = [ToggleProbe]::GetDpiForSystem() / 96.0
$sm = [System.Windows.Forms.SystemInformation]::VirtualScreen
Write-Host ("screen {0}x{1} px, dpi {2} -> scale {3}" -f $sm.Width, $sm.Height, [ToggleProbe]::GetDpiForSystem(), $scale)

function Dip([double]$v) { return [int][Math]::Round($v * $scale) }

$proc = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue
if (-not $proc) { Write-Host 'FAIL: TaskbarLyrics is not running'; exit 1 }

$hwnd = [ToggleProbe]::FindSettingsWindow([uint32]$proc.Id, 'TaskbarLyrics Overlay')
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Host 'FAIL: no settings window found (start the app with --settings)'
    exit 1
}

$rect = New-Object ToggleProbe+RECT
[void][ToggleProbe]::GetWindowRect($hwnd, [ref]$rect)
if ($rect.Left -le -20000 -or $rect.Top -le -20000 -or $rect.Right -le $rect.Left) {
    # A minimized window reports the -32000 sentinel (scaled by DPI here), so restore it
    # before measuring anything: there is no pane to look at while it is minimised.
    Write-Host 'settings window is minimised - restoring it'
    [void][ToggleProbe]::ShowWindow($hwnd, 9)   # SW_RESTORE
    [void][ToggleProbe]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 700
    [void][ToggleProbe]::GetWindowRect($hwnd, [ref]$rect)
}

# The capture reads the screen, so the window must actually be on top.  SetForegroundWindow
# is refused when the caller is not the foreground process, so raise it with SetWindowPos
# (HWND_TOPMOST) for the shots and drop it back at the end.
[void][ToggleProbe]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0043)   # HWND_TOPMOST | NOMOVE|NOSIZE|SHOWWINDOW
Start-Sleep -Milliseconds 400
[void][ToggleProbe]::GetWindowRect($hwnd, [ref]$rect)
$winW = $rect.Right - $rect.Left
$winH = $rect.Bottom - $rect.Top
Write-Host ("settings window at ({0},{1}) {2}x{3} px" -f $rect.Left, $rect.Top, $winW, $winH)

# --- capture helpers -------------------------------------------------------
function Grab([int]$x, [int]$y, [int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle(0, 0, $w, $h)),
                          [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $stride = $data.Stride
    $bmp.UnlockBits($data)
    $bmp.Dispose()
    return @{ px = $bytes; stride = $stride; w = $w; h = $h }
}

function Luma($px, $stride, [int]$x, [int]$y) {
    $i = $y * $stride + $x * 4
    return 0.114 * $px[$i] + 0.587 * $px[$i + 1] + 0.299 * $px[$i + 2]
}

function Shot() { return (Grab $rect.Left $rect.Top $winW $winH) }

# Counts the pixels clearly darker than the pane inside a box around a candidate centre.
function Tinted($px, $stride, [int]$cx, [int]$cy, [double]$paneLuma, [int]$pad) {
    $n = 0
    $top = [Math]::Max(0, $cy - $pad)
    $bottom = [Math]::Min($winH - 1, $cy + $pad)
    $left = [Math]::Max(0, $cx - $pad)
    $right = [Math]::Min($winW - 1, $cx + $pad)
    for ($y = $top; $y -le $bottom; $y++) {
        for ($x = $left; $x -le $right; $x++) {
            if ((Luma $px $stride $x $y) -le ($paneLuma - 5)) { $n++ }
        }
    }
    return $n
}

# --- 1. where is the pane, and what does it look like? ----------------------
[void][ToggleProbe]::SetCursorPos($rect.Left + (Dip 400), $rect.Bottom - (Dip 160))
Start-Sleep -Milliseconds 700
$rest = Shot

# The pane's right edge: walk left from the content side until the pane colour holds.
# Scanned BELOW the header (where the pane is uniform), because in the header itself the
# app icon and the title sit in the same row and would end the walk early.
$scanRow = Dip 48
$paneLuma = Luma $rest.px $rest.stride 6 $scanRow
$contentLuma = Luma $rest.px $rest.stride ($winW - 8) $scanRow
if ([Math]::Abs($paneLuma - $contentLuma) -lt 3) {
    # Distinguishable pane and content tones are what this probe reads; when they are equal
    # something else is on screen (typically a window covering the settings window).
    Write-Host ("FAIL: settings window appears occluded (pane luma {0:N0} == content luma {1:N0})" -f $paneLuma, $contentLuma)
    [void][ToggleProbe]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0043)
    exit 1
}
$paneRight = -1
for ($x = $winW - 8; $x -ge 60; $x--) {
    $hit = 0
    for ($k = 0; $k -lt 3; $k++) {
        if ([Math]::Abs((Luma $rest.px $rest.stride ($x - $k) $scanRow) - $paneLuma) -le 4) { $hit++ }
    }
    if ($hit -eq 3) { $paneRight = $x; break }
}
if ($paneRight -lt 0) {
    Write-Host 'FAIL: could not find the nav pane edge'
    [void][ToggleProbe]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0043)
    exit 1
}
Write-Host ("nav pane: colour luma {0:N0}, right edge {1} px (= {2:N1} DIP), content luma {3:N0}" -f `
    $paneLuma, $paneRight, ($paneRight / $scale), $contentLuma)

# --- 2. find the button by hovering candidates ------------------------------
# The plate is a big filled disc (about 28 DIP across); its glyph is a thin 19x15 DIP
# outline. Counting tinted pixels therefore separates "hovered the button" from "hovered
# near it", and the position is measured rather than assumed.
$padDip = 20
$pad = Dip $padDip
$best = $null
foreach ($dx in @(22, 28, 34, 40)) {
    foreach ($dy in @(12, 16, 20, 24)) {
        $cx = $paneRight - (Dip $dx)
        $cy = Dip $dy
        if ($cx -lt $pad -or $cy -lt 2) { continue }
        [void][ToggleProbe]::SetCursorPos($rect.Left + $cx, $rect.Top + $cy)
        Start-Sleep -Milliseconds 420
        $shot = Shot
        $n = Tinted $shot.px $shot.stride $cx $cy $paneLuma $pad
        Write-Host ("  hover ({0,3},{1,2}) px -> {2,5} tinted px" -f $cx, $cy, $n)
        if ($null -eq $best -or $n -gt $best.n) {
            $best = @{ n = $n; cx = $cx; cy = $cy; shot = $shot }
        }
    }
}

if ($null -eq $best -or $best.n -lt 400) {
    $got = if ($null -eq $best) { 0 } else { $best.n }
    Write-Host ("FAIL: the toggle never showed a hover plate (best {0} tinted px) - is the pane toggle there?" -f $got)
    [void][ToggleProbe]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0043)
    exit 1
}
$centreX = $best.cx
$centreY = $best.cy
Write-Host ("toggle hovered at ({0},{1}) px with {2} tinted px" -f $centreX, $centreY, $best.n)

# --- 3. the plate must be invisible at rest ---------------------------------
# The glyph itself is dark, so "tinted" is non-zero at rest too; what must not be there is
# the filled disc, which lights up roughly three times as many pixels as the outline.
$tintedAtRest = Tinted $rest.px $rest.stride $centreX $centreY $paneLuma $pad

# --- 4. measure the plate row by row ---------------------------------------
$hover = $best.shot
# The header is 32 DIP tall; stay inside it and clear of the window's own border rows (the
# 1 px frame line is dark all the way across and would read as a 49 px "span").
$bandTop = Dip 2
$bandBottom = Dip 36
$allSpans = @()
for ($y = $bandTop; $y -le $bandBottom; $y++) {
    $first = -1; $last = -1
    for ($x = [Math]::Max(0, $centreX - $pad); $x -le [Math]::Min($winW - 1, $centreX + $pad); $x++) {
        if ((Luma $hover.px $hover.stride $x $y) -le ($paneLuma - 5)) {
            if ($first -lt 0) { $first = $x }
            $last = $x
        }
    }
    $allSpans += [pscustomobject]@{ y = $y; span = if ($first -lt 0) { 0 } else { $last - $first + 1 } }
}

# The plate is one contiguous block of tinted rows; the glyph's own rows merge into it,
# while the title text to its left is outside the sampled columns anyway.
$runs = @()
$run = @()
foreach ($row in $allSpans) {
    if ($row.span -gt 0) { $run += $row }
    elseif ($run.Count -gt 0) { $runs += , $run; $run = @() }
}
if ($run.Count -gt 0) { $runs += , $run }
$spans = if ($runs.Count -gt 0) { $runs | Sort-Object -Property { ($_ | Measure-Object -Property span -Maximum).Maximum } -Descending | Select-Object -First 1 } else { @() }

$maxSpan = if ($spans.Count -gt 0) { ($spans | Measure-Object -Property span -Maximum).Maximum } else { 0 }
$spanRows = @($spans | Where-Object { $_.span -gt 0 })
$plateH = if ($spanRows.Count -gt 0) { $spanRows[-1].y - $spanRows[0].y + 1 } else { 0 }
$atMax = @($spans | Where-Object { $_.span -ge ($maxSpan * 0.95) }).Count
if ($spanRows.Count -gt 0) {
    Write-Host ("plate rows {0}..{1} px" -f $spanRows[0].y, $spanRows[-1].y)
}

Write-Host ''
Write-Host ("plate: max span {0} px ({1:N1} DIP), height {2} px ({3:N1} DIP), rows at max {4}" -f `
    $maxSpan, ($maxSpan / $scale), $plateH, ($plateH / $scale), $atMax)
Write-Host ("profile (rows with ink): {0}" -f (($spanRows | ForEach-Object { $_.span }) -join ' '))
Write-Host ''

$failed = 0
function Check([bool]$ok, [string]$label, [string]$detail) {
    if ($ok) { Write-Host ("  PASS  {0}  {1}" -f $label, $detail) }
    else { Write-Host ("  FAIL  {0}  {1}" -f $label, $detail); $script:failed++ }
}

$expect = 28 * $scale
Check ($tintedAtRest -lt ($best.n * 0.6)) 'no plate at rest (only the glyph outline)' `
    ("{0} tinted px at rest vs {1} hovered" -f $tintedAtRest, $best.n)
Check ([Math]::Abs($maxSpan - $expect) -le 3) 'the plate is 28 DIP wide' ("{0:N1} DIP, expected 28" -f ($maxSpan / $scale))
Check ([Math]::Abs($plateH - $expect) -le 4) 'the plate is 28 DIP tall (square box)' ("{0:N1} DIP" -f ($plateH / $scale))

# Roundness, measured where a rectangle and a disc actually differ: a disc starts and ends
# as a point (narrow first/last rows) and its mean chord is pi/4 of the diameter.  A
# rectangle gives full-width end rows and a mean chord equal to its width.  Counting "rows
# near the maximum" would not separate them: a 35 px disc legitimately has ~11 such rows.
if ($spanRows.Count -ge 6) {
    $firstSpan = $spanRows[0].span
    $lastSpan = $spanRows[-1].span
    $narrow = [Math]::Max(0.35 * $maxSpan, 2)
    Check (($firstSpan -le $narrow) -and ($lastSpan -le $narrow)) 'the plate ends in points (disc, not rectangle)' `
        ("first {0} px, last {1} px, max {2} px" -f $firstSpan, $lastSpan, $maxSpan)

    $meanChord = ($spanRows | Measure-Object -Property span -Average).Average
    $expectMean = [Math]::PI / 4.0 * $maxSpan
    Check ([Math]::Abs($meanChord - $expectMean) -le (0.12 * $expectMean)) 'the mean chord matches a disc' `
        ("{0:N1} px vs {1:N1} px (pi/4 of max)" -f $meanChord, $expectMean)

    $widest = ($spans | Sort-Object -Property span -Descending)[0].y
    $mid = ($spanRows[0].y + $spanRows[-1].y) / 2.0
    Check ([Math]::Abs($widest - $mid) -le ($plateH / 6.0)) 'the widest row is in the middle' ("y={0} vs centre {1:N1}" -f $widest, $mid)
} else {
    Check $false 'the plate ends in points (disc, not rectangle)' 'not enough rows with ink'
    Check $false 'the mean chord matches a disc' 'not enough rows with ink'
    Check $false 'the widest row is in the middle' 'not enough rows with ink'
}

# Leave the user's desktop as we found it: the settings window was raised only for the shots.
[void][ToggleProbe]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0043)   # HWND_NOTOPMOST
Write-Host ''
if ($failed -eq 0) { Write-Host 'all toggle-disc checks passed'; exit 0 }
Write-Host ("{0} toggle-disc check(s) failed" -f $failed)
exit 1
