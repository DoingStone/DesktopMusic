$ErrorActionPreference = 'Stop'

# Verifies the two reported faults by reading the APPLICATION's own state, not screen
# pixels: a successful sash drag persists NavWidth, and a successful close hides the
# window. Screen sampling was unreliable because another full-screen window sits on top of
# the settings window, so GetPixel was reading that window instead.
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public class W
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }

    static IntPtr found;
    public static IntPtr ByPid(uint pid, string part) {
        found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid) {
                var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
                if (sb.ToString().Contains(part)) { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string RectOf(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        return string.Format("({0},{1})-({2},{3}) {4}x{5}", r.L, r.T, r.R, r.B, r.R-r.L, r.B-r.T);
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(220);
        mouse_event(0x02, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(600);
    }

    public static void Drag(int x, int y, int dx) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(300);
        mouse_event(0x02, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(250);
        for (int i = 1; i <= 20; i++) {
            SetCursorPos(x + dx * i / 20, y);
            System.Threading.Thread.Sleep(40);
        }
        mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(800);
    }
}
"@
[void][W]::E()

$title = [string][char]0x8BBE + [string][char]0x7F6E
$settingsPath = "$env:APPDATA\TaskbarLyrics\settings.json"

function Get-NavWidth {
    if (-not (Test-Path $settingsPath)) { return -1 }
    $j = Get-Content $settingsPath -Raw | ConvertFrom-Json
    if ($j.PSObject.Properties.Name -contains 'NavWidth') { return [double]$j.NavWidth }
    return -1
}

$proc = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowTitle -like "*$title*" } | Select-Object -First 1
if (-not $proc) { Write-Host "settings window not running"; exit 1 }

$h = [W]::ByPid([uint32]$proc.Id, $title)
if ($h -eq [IntPtr]::Zero) { Write-Host "hwnd not found"; exit 1 }

# Raise above everything so injected input lands here.
[void][W]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x53)
Start-Sleep -Milliseconds 900

$rc = New-Object W+RECT
[void][W]::GetWindowRect($h, [ref]$rc)
$winL = [int]$rc.L; $winT = [int]$rc.T
$scale = ($rc.R - $rc.L) / 900.0
$midY = $winT + [int](300 * $scale)

Write-Host ("window {0}  scale {1:N3}" -f ([W]::RectOf($h)), $scale)
Write-Host ""

# ---------- test 1: drag the sash ----------
$before = Get-NavWidth
$sashX = $winL + [int]($before * $scale) + 2   # 2 px into the 5 px band
Write-Host ("TEST 1  drag sash:  NavWidth before = {0}, pressing at x={1}" -f $before, $sashX)
[W]::Drag($sashX, $midY, [int](120 * $scale))
$after = Get-NavWidth
$rectAfter = [W]::RectOf($h)
Write-Host ("        NavWidth after  = {0}   window {1}" -f $after, $rectAfter)

if ($after -gt ($before + 60)) { Write-Host "        RESULT: PASS - the sash resized the pane" }
elseif ($rectAfter -ne ([W]::RectOf($h))) { Write-Host "        RESULT: FAIL - window moved" }
else { Write-Host "        RESULT: FAIL - sash did not resize (NavWidth unchanged)" }

Write-Host ""

# ---------- test 2: click the caption close button ----------
$rc2 = New-Object W+RECT
[void][W]::GetWindowRect($h, [ref]$rc2)
# Close button: 46 DIP wide, right edge flush with the window, row is 32 DIP tall.
$closeX = [int]($rc2.R) - [int](23 * $scale)
$closeY = [int]$rc2.T + [int](16 * $scale)
$visibleBefore = [W]::IsWindowVisible($h)
Write-Host ("TEST 2  click caption close at ({0},{1});  visible before = {2}" -f $closeX, $closeY, $visibleBefore)
[W]::Click($closeX, $closeY)
Start-Sleep -Milliseconds 900
$visibleAfter = [W]::IsWindowVisible($h)
Write-Host ("        visible after = {0}" -f $visibleAfter)
if ($visibleBefore -and -not $visibleAfter) { Write-Host "        RESULT: PASS - the caption close button worked" }
else { Write-Host "        RESULT: FAIL - window still visible, the button did not fire" }
