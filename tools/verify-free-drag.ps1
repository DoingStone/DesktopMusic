<#
  verify-free-drag.ps1 - real-desktop check for the "draggable" switch and free placement.

  Case 1 (locked / click-through strip):
    hovering still reveals the transport controls and makes room for them, leaving hides
    them again and brings the lyrics back to the middle of the strip.

  Case 2 (interactive strip):
    dragging upwards leaves the taskbar band, the position snaps to the screen edges while
    dragging, and dropping the strip over the band docks it back onto the taskbar.

  Run it with the app closed: it starts its own instances against an isolated settings file
  (TBL_SETTINGS) and removes the diag log first so stale lines cannot be mistaken for this
  run's evidence.
#>
param([switch]$KeepSettings)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\TaskbarLyrics.App\bin\Debug\net8.0-windows10.0.19041.0\TaskbarLyrics.exe'
$log = Join-Path $env:TEMP 'taskbar-lyrics-diag.log'
$settingsPath = Join-Path $env:TEMP 'tbl-free-drag-settings.json'
$script:fail = 0

if (-not (Test-Path $exe)) { Write-Host "FAIL: app is not built at $exe"; exit 1 }
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Probe {
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetDpiForSystem();
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr dc, int i);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004;

    // The overlay is the only visible window of the process whose class is a WPF HwndWrapper
    // and which is as wide as the strip; anything narrower is chrome we do not care about.
    public static IntPtr FindOverlay(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            uint wpid;
            GetWindowThreadProcessId(h, out wpid);
            if (wpid != pid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            if (!sb.ToString().StartsWith("HwndWrapper")) return true;
            RECT r;
            GetWindowRect(h, out r);
            if (r.Right - r.Left < 200) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static int[] Rect(IntPtr h) {
        RECT r;
        GetWindowRect(h, out r);
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }

    // 118 / 117 are DESKTOPHORZRES / DESKTOPVERTRES: physical pixels, never virtualised.
    public static int ScreenW() { IntPtr dc = GetDC(IntPtr.Zero); int v = GetDeviceCaps(dc, 118); ReleaseDC(IntPtr.Zero, dc); return v; }
    public static int ScreenH() { IntPtr dc = GetDC(IntPtr.Zero); int v = GetDeviceCaps(dc, 117); ReleaseDC(IntPtr.Zero, dc); return v; }
}
'@

[Probe]::SetProcessDPIAware() | Out-Null
$scale = [Probe]::GetDpiForSystem() / 96.0
$screenW = [Probe]::ScreenW()
$screenH = [Probe]::ScreenH()
Write-Host ("screen {0}x{1} px, dpi {2} -> scale {3}" -f $screenW, $screenH, [Probe]::GetDpiForSystem(), $scale)

function Write-Settings([bool]$locked) {
    # Only the keys this test cares about: everything else keeps its default.
    $json = [ordered]@{
        Visible            = $true
        ShowWhenPaused     = $true
        HideWhenNoLyrics   = $false
        Locked             = $locked
        HoverRevealControls = $true
        ShowTransportControls = $true
        ShowSongProgress   = $true
        ShowSongTitle      = $false
        ShowSongArtist     = $false
        ShowCoverArt       = $false
        ShowBackground     = $false
        ShowTranslation    = $false
        ShowContextLines   = $false
        Width              = 460
        Height             = 0
        OffsetX            = 0
        OffsetY            = 0
        VerticalAlign      = 0
        PlaceAboveTaskbar  = $false
        FreePosition       = $false
        FreeX              = 0
        FreeY              = 0
        SnapToEdges        = $true
    } | ConvertTo-Json
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($settingsPath, $json, $utf8)
}

function Start-App([bool]$locked) {
    Write-Settings $locked
    Remove-Item $log -ErrorAction SilentlyContinue
    $env:TBL_DIAG = '1'
    $env:TBL_SETTINGS = $settingsPath
    $proc = Start-Process -FilePath $exe -PassThru
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        if ($proc.HasExited) { Write-Host "FAIL: the app exited (code $($proc.ExitCode))"; exit 1 }
        $h = [Probe]::FindOverlay([uint32]$proc.Id)
        if ($h -ne [IntPtr]::Zero) {
            Start-Sleep -Milliseconds 900
            return @{ Proc = $proc; Handle = [Probe]::FindOverlay([uint32]$proc.Id) }
        }
    }
    Write-Host 'FAIL: no overlay window appeared'
    exit 1
}

function Log-Text {
    if (Test-Path $log) { return [System.IO.File]::ReadAllText($log) }
    return ''
}

function Move-Cursor([int]$x, [int]$y) {
    [Probe]::SetCursorPos($x, $y) | Out-Null
    # SetCursorPos does not always produce WM_MOUSEMOVE for a captured window; a relative
    # move either way does, and the two cancel out.
    [Probe]::mouse_event([Probe]::MOVE, 1, 0, 0, [UIntPtr]::Zero)
    [Probe]::mouse_event([Probe]::MOVE, -1, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 70
}

function Drag([int]$fromX, [int]$fromY, [int]$toX, [int]$toY) {
    Move-Cursor $fromX $fromY
    Start-Sleep -Milliseconds 250
    [Probe]::mouse_event([Probe]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
    $steps = 14
    for ($i = 1; $i -le $steps; $i++) {
        $x = [int]($fromX + ($toX - $fromX) * $i / $steps)
        $y = [int]($fromY + ($toY - $fromY) * $i / $steps)
        Move-Cursor $x $y
    }
    Start-Sleep -Milliseconds 300
    [Probe]::mouse_event([Probe]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 700
}

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("PASS {0}  {1}" -f $name, $detail) }
    else { Write-Host ("FAIL {0}  {1}" -f $name, $detail); $script:fail++ }
}

# ---------------------------------------------------------------- case 1: locked strip
Write-Host "`n== case 1: locked (click-through) strip still reveals and hides its controls =="
$app = Start-App $true
$rect = [Probe]::Rect($app.Handle)
Write-Host ("overlay {0},{1} {2}x{3} px (locked)" -f $rect[0], $rect[1], $rect[2], $rect[3])

$hoverX = [int]($rect[0] + $rect[2] * 0.75)   # right of the transport cluster: lyrics area
$hoverY = [int]($rect[1] + $rect[3] / 2)
Move-Cursor $hoverX $hoverY
Start-Sleep -Milliseconds 1600
$text = Log-Text
# "controls hover-revealed (shown=…)" is only the mode line written when the settings are
# applied; the fade itself reports "controls revealed" / "controls hidden".
Check 'hovering reveals the controls while locked' ($text -match 'controls revealed') "at ($hoverX,$hoverY)"
$inset = -1.0
if ($text -match '(?s).*lyric inset=([\d.]+)') { $inset = [double]$Matches[1] }   # greedy: last one
Check 'the lyrics make room for the controls' ($inset -gt 1.0) ("inset=$inset DIP")

Move-Cursor ($screenW - 40) 200              # somewhere far away from the strip
Start-Sleep -Milliseconds 2200
$text = Log-Text
$revealed = $text.LastIndexOf('controls revealed')
$hidden = $text.LastIndexOf('controls hidden')
Check 'leaving hides the controls again while locked' ($revealed -ge 0 -and $hidden -gt $revealed) `
    "revealed at $revealed, hidden at $hidden"
$tail = ''
if ($hidden -ge 0) { $tail = $text.Substring($hidden) }
$backInset = -1.0
if ($tail -match '(?s).*lyric inset=([\d.]+)') { $backInset = [double]$Matches[1] }
Check 'the lyrics go back to the middle of the strip' ($backInset -ge 0 -and $backInset -le 0.5) `
    "inset after leaving: $backInset DIP"

Stop-Process -Id $app.Proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# ------------------------------------------------------- case 2: drag, snap, dock back
Write-Host "`n== case 2: drag the strip off the taskbar, snap to the edges, dock back =="
$app2 = Start-App $false
$h2 = $app2.Handle
$strip = [Probe]::Rect($h2)
$band = [Probe]::Rect([Probe]::FindWindow('Shell_TrayWnd', $null))
Write-Host ("overlay {0},{1} {2}x{3} px   taskbar band top={4} height={5}" -f $strip[0], $strip[1], $strip[2], $strip[3], $band[1], $band[3])

Check 'the strip starts docked inside the band' `
    (($strip[1] -ge ($band[1] - 2)) -and (($strip[1] + $strip[3]) -le ($band[1] + $band[3] + 2))) `
    ("top={0} bottom={1} band={2}..{3}" -f $strip[1], ($strip[1] + $strip[3]), $band[1], ($band[1] + $band[3]))

$pad = [int][Math]::Round(8 * $scale)        # EdgePadding = 8 DIP
$off = [int][Math]::Round(7 * $scale)        # stop 7 DIP short of the snap lines
$targetX = $screenW - $strip[2] - $pad + $off
$targetY = $pad + $off
$grabX = [int]($strip[0] + $strip[2] * 0.75)
$grabY = [int]($strip[1] + $strip[3] / 2)
Drag $grabX $grabY ($grabX + $targetX - $strip[0]) ($grabY + $targetY - $strip[1])

$text2 = Log-Text
Check 'dragging away from the band switches to the free position' ($text2 -match 'drag left the taskbar: free position') ''
$samples = ([regex]::Matches($text2, 'drag free x=')).Count
Check 'the strip follows the cursor while dragging' ($samples -ge 3) ("$samples free-position samples")
$free = [Probe]::Rect($h2)
Check 'the strip left the taskbar band' (($free[1] + $free[3]) -lt $band[1]) `
    ("top={0} bottom={1} band top={2}" -f $free[1], ($free[1] + $free[3]), $band[1])
Check 'the top edge snapped' ([Math]::Abs($free[1] - $pad) -le 2) ("top={0} expected {1}" -f $free[1], $pad)
Check 'the right edge snapped' ([Math]::Abs(($free[0] + $free[2]) - ($screenW - $pad)) -le 2) `
    ("right={0} expected {1}" -f ($free[0] + $free[2]), ($screenW - $pad))

$dropY = [int]($band[1] + $band[3] / 2)
Drag ([int]($free[0] + $free[2] * 0.75)) ([int]($free[1] + $free[3] / 2)) ([int]($free[0] + $free[2] * 0.75)) $dropY
$text3 = Log-Text
Check 'dropping the strip over the band docks it back' ($text3 -match 'docked back to the taskbar') ''
$docked = [Probe]::Rect($h2)
Check 'the docked strip is back inside the band' `
    (($docked[1] -ge ($band[1] - 2)) -and (($docked[1] + $docked[3]) -le ($band[1] + $band[3] + 2))) `
    ("top={0} bottom={1} band={2}..{3}" -f $docked[1], ($docked[1] + $docked[3]), $band[1], ($band[1] + $band[3]))

Stop-Process -Id $app2.Proc.Id -Force -ErrorAction SilentlyContinue
if (-not $KeepSettings) { Remove-Item $settingsPath -ErrorAction SilentlyContinue }

Write-Host ''
if ($script:fail -eq 0) { Write-Host 'all free-drag checks passed'; exit 0 }
Write-Host "$script:fail free-drag check(s) failed"
exit 1
