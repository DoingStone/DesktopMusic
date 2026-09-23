param(
    # Also park the cursor on the strip and photograph the hover state. Saves and
    # restores the cursor position, but the pointer does move for a moment.
    [switch]$HoverTest,
    [string]$Exe = 'E:\Qianmory\Desktop\DesktopMusic\src\TaskbarLyrics.App\bin\Debug\net8.0-windows10.0.19041.0\TaskbarLyrics.exe',
    [string]$Out = 'E:\Qianmory\Desktop\DesktopMusic\artifacts\p0'
)

# Verifies the P0 look on the live taskbar by photographing the overlay's own rectangle
# and measuring it directly, with no second reference shot:
#
#   * a dark backdrop would make most of the rectangle dark, so the light fraction is
#     the plate test (Windows shows the taskbar through a transparent strip);
#   * the ink must be darker than the bar it sits on - that is the adaptive palette;
#   * the transport controls live in the leftmost ~130 device pixels, so any ink there
#     means they are never hidden.
#
# Photographing the same rectangle before and after the app exits looked tempting but is
# not usable: the taskbar repaints underneath (icons, hover, animations), which swamps
# the signal. The measurements below need no reference frame, and the hover switch adds
# the one comparison that is stable - idle against hovered, both with the app running.

$ErrorActionPreference = 'Continue'

Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class P0V {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][P0V]::E()
Add-Type -AssemblyName System.Drawing

$log = Join-Path $env:TEMP 'taskbar-lyrics-diag.log'

function Get-OverlayRect([int]$target) {
    $script:found = $null
    [void][P0V]::EnumWindows({ param($h, $l)
            $pp = 0; [void][P0V]::GetWindowThreadProcessId($h, [ref]$pp)
            if ($pp -eq $target) {
                $t = New-Object Text.StringBuilder 512; [void][P0V]::GetWindowText($h, $t, 512)
                if ($t.ToString() -eq 'TaskbarLyrics Overlay' -and [P0V]::IsWindowVisible($h)) {
                    $r = New-Object P0V+RECT; [void][P0V]::GetWindowRect($h, [ref]$r)
                    $script:found = $r
                }
            }
            return $true
        }, [IntPtr]::Zero)
    return $script:found
}

function Grab([int]$x, [int]$y, [int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    return $bmp
}

function Luma($c) { return 0.2126 * $c.R + 0.7152 * $c.G + 0.0722 * $c.B }

# Ink = anything clearly darker than a mid-tone background. Counted per column too, so a
# clean left band can be told apart from a strip whose lyrics happen to start late. The
# blue count is the progress bar's fill colour (#FF3ABEFF): the one thing in the strip
# that neither the lyrics nor the taskbar underneath can produce.
function Measure-Strip($bmp, [int]$w, [int]$h, [int]$leftBand) {
    $dark = 0; $mid = 0; $light = 0; $band = 0; $blue = 0; $ink0 = 0; $ink1 = 0
    $minX = $w; $maxX = -1; $minY = $h; $maxY = -1
    $darkest = 999.0; $lightSum = 0.0
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y); $l = Luma $c
            if ($l -lt 140) {
                $dark++
                if ($x -lt $leftBand) { $band++ }
                if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
                if ($l -lt $darkest) { $darkest = $l }
            }
            elseif ($l -lt 190) { $mid++ }
            else { $light++; $lightSum += $l }
            if ($c.B -gt 180 -and $c.R -lt 140 -and $c.G -gt 130) { $blue++ }
            # The adaptive ink itself: near-neutral (not a taskbar icon) and at the far
            # end from the bar it sits on. This is what the acceptance suite asserts.
            $spread = [Math]::Max($c.R, [Math]::Max($c.G, $c.B)) - [Math]::Min($c.R, [Math]::Min($c.G, $c.B))
            if ($spread -le 26) {
                if ($l -le 60) { $ink0++ } elseif ($l -ge 200) { $ink1++ }
            }
        }
    }
    $total = $w * $h
    return [pscustomobject]@{
        W      = $w; H = $h; Total = $total
        Dark   = $dark; Mid = $mid; Light = $light
        Band   = $band; Blue = $blue
        InkOnLight = $ink0; InkOnDark = $ink1
        DarkF  = $dark / $total
        LightF = $light / $total
        Box    = if ($dark -gt 0) { "($minX,$minY)-($maxX,$maxY)" } else { 'none' }
        Darkest = $darkest
        BarLuma = if ($light -gt 0) { $lightSum / $light } else { 0 }
    }
}

function Grab-Strip($r) { return (Grab $r.L $r.T ($r.R - $r.L) ($r.B - $r.T)) }

Write-Host "=== P0 look verification ==="
Write-Host "exe : $Exe"
if (-not (Test-Path $Exe)) { Write-Host "RESULT: FAIL - exe not found (build first)"; exit 1 }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$running = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue
if (-not $running) {
    Remove-Item $log -ErrorAction SilentlyContinue
    $env:TBL_DIAG = '1'
    $running = Start-Process -FilePath $Exe -ArgumentList '--tray' -PassThru
    Write-Host "started the app (pid $($running.Id))"
    Start-Sleep -Seconds 12
} else {
    Write-Host "using the running app (pid $($running.Id))"
}

$r = Get-OverlayRect $running.Id
if (-not $r) {
    Write-Host "RESULT: FAIL - overlay window not found"
    exit 1
}
$w = $r.R - $r.L; $h = $r.B - $r.T
Write-Host ("rect: ({0},{1}) {2}x{3}  [device pixels]" -f $r.L, $r.T, $w, $h)

# The transport panel is three 28-DIP buttons plus the progress row, anchored at the
# left margin: ~130 device pixels at this scale. Lyrics are centred across the full
# width, so ink this far left can only be the controls.
$leftBand = [int](130 * ($w / 460))

$idle = Grab-Strip $r
$idle.Save((Join-Path $Out 'strip-idle.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$m = Measure-Strip $idle $w $h $leftBand

Write-Host ""
Write-Host "--- strip measured directly ---"
Write-Host ("  ink (<140 luma)   : {0} px ({1:P1})   bbox {2}" -f $m.Dark, $m.DarkF, $m.Box)
Write-Host ("  partial (140-190) : {0} px ({1:P1})" -f $m.Mid, ($m.Mid / $m.Total))
Write-Host ("  see-through       : {0} px ({1:P1})  [a backdrop plate would drive this to ~0]" -f $m.Light, $m.LightF)
Write-Host ("  bar luma / darkest: {0:N1} / {1:N1}" -f $m.BarLuma, $m.Darkest)
Write-Host ("  ink in left band  : {0} px  (x<{1}; resident controls would show here)" -f $m.Band, $leftBand)
Write-Host ("  progress-bar blue : {0} px  (controls hidden should be 0)" -f $m.Blue)
Write-Host ("  adaptive ink      : {0} px dark-on-light, {1} px light-on-dark  (bar luma {2:N1})" -f $m.InkOnLight, $m.InkOnDark, $m.BarLuma)

$palette = Select-String -Path $log -Pattern '\[overlay\] palette' -ErrorAction SilentlyContinue | Select-Object -Last 1
$reveal = Select-String -Path $log -Pattern '\[overlay\] controls hover-revealed' -ErrorAction SilentlyContinue | Select-Object -Last 1
$exceptions = (Select-String -Path $log -Pattern 'EXCEPTION' -ErrorAction SilentlyContinue | Measure-Object).Count
$rendering = (Select-String -Path $log -Pattern '\[overlay\] Render pos=' -ErrorAction SilentlyContinue | Measure-Object).Count
if ($palette) { Write-Host ("  palette log       : " + $palette.Line.Trim()) }
if ($reveal) { Write-Host ("  reveal log        : " + $reveal.Line.Trim()) }
Write-Host ("  render ticks      : {0}   exceptions: {1}" -f $rendering, $exceptions)

$verdicts = @()
$verdicts += [pscustomobject]@{ Name = 'strip is see-through (no backdrop plate)'; Pass = ($m.LightF -gt 0.70) }
$verdicts += [pscustomobject]@{ Name = 'ink is darker than the bar (adaptive palette)'; Pass = ($m.DarkF -gt 0.01 -and $m.Darkest -lt ($m.BarLuma - 60)) }
$verdicts += [pscustomobject]@{ Name = 'adaptive ink is neutral and far from the bar'; Pass = $(if ($m.BarLuma -ge 128) { $m.InkOnLight -gt 0 } else { $m.InkOnDark -gt 0 }) }
$verdicts += [pscustomobject]@{ Name = 'controls start hidden'; Pass = [bool]($reveal -and $reveal.Line -match 'shown=False') }
$verdicts += [pscustomobject]@{ Name = 'adaptive palette selected by sampling'; Pass = [bool]($palette -and $palette.Line -match 'auto=True') }
$verdicts += [pscustomobject]@{ Name = 'lyrics actually rendering'; Pass = ($rendering -gt 0) }
$verdicts += [pscustomobject]@{ Name = 'no render exceptions'; Pass = ($exceptions -eq 0) }

if ($HoverTest) {
    $saved = New-Object P0V+POINT
    [void][P0V]::GetCursorPos([ref]$saved)
    [void][P0V]::SetCursorPos($r.L + 30, $r.T + [int]($h / 2))
    Start-Sleep -Milliseconds 900
    $hover = Grab-Strip $r
    [void][P0V]::SetCursorPos($saved.X, $saved.Y)
    Start-Sleep -Milliseconds 400
    $hover.Save((Join-Path $Out 'strip-hover.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $mh = Measure-Strip $hover $w $h $leftBand
    Write-Host ""
    Write-Host "--- hovered strip ---"
    Write-Host ("  ink in left band  : {0} px (idle had {1})" -f $mh.Band, $m.Band)
    Write-Host ("  progress-bar blue : {0} px (idle had {1})" -f $mh.Blue, $m.Blue)
    Write-Host ("  ink total         : {0} px (idle had {1})" -f $mh.Dark, $m.Dark)
    # The progress bar is the reliable witness: ink in the band can also be a taskbar
    # icon showing through the transparent strip, but nothing behind it paints #3ABEFF.
    $verdicts += [pscustomobject]@{ Name = 'hover reveals the controls'; Pass = ($mh.Blue -gt 10 -and $mh.Blue -gt ($m.Blue + 5)) }
    $hover.Dispose()
}

Write-Host ""
foreach ($v in $verdicts) {
    Write-Host ("  [{0}] {1}" -f $(if ($v.Pass) { 'PASS' } else { 'FAIL' }), $v.Name)
}
$failed = ($verdicts | Where-Object { -not $_.Pass }).Count
Write-Host ""
if ($failed -eq 0) { Write-Host "RESULT: PASS - all look checks passed" }
else { Write-Host ("RESULT: FAIL - {0} check(s) failed" -f $failed) }

$idle.Dispose()

