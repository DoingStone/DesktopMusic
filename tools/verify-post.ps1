$ErrorActionPreference = 'Stop'

# Sends mouse messages straight to the window instead of moving the real cursor.
#
# Injected cursor input cannot be used here: a full-screen window sits above the settings
# window and owns activation, so SetCursorPos + mouse_event lands on that window instead.
# Posting WM_MOUSE* to the target HWND bypasses z-order entirely and still exercises the
# application's own input path, because WPF reads the hit point from lParam.
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public class Post
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);

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

    const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    const int MK_LBUTTON = 0x0001;

    static IntPtr Pack(int x, int y) { return (IntPtr)((y << 16) | (x & 0xFFFF)); }

    public static void MoveTo(IntPtr h, int x, int y) {
        PostMessage(h, WM_MOUSEMOVE, IntPtr.Zero, Pack(x, y));
        System.Threading.Thread.Sleep(30);
    }

    /// Mouse-down, incremental moves, mouse-up - all in CLIENT pixels.
    public static void Drag(IntPtr h, int x0, int y0, int dx, int steps) {
        PostMessage(h, WM_MOUSEMOVE, IntPtr.Zero, Pack(x0, y0));
        System.Threading.Thread.Sleep(120);
        PostMessage(h, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, Pack(x0, y0));
        System.Threading.Thread.Sleep(150);
        for (int i = 1; i <= steps; i++) {
            PostMessage(h, WM_MOUSEMOVE, (IntPtr)MK_LBUTTON, Pack(x0 + dx * i / steps, y0));
            System.Threading.Thread.Sleep(45);
        }
        PostMessage(h, WM_LBUTTONUP, IntPtr.Zero, Pack(x0 + dx, y0));
        System.Threading.Thread.Sleep(500);
    }

    public static void Click(IntPtr h, int x, int y) {
        PostMessage(h, WM_MOUSEMOVE, IntPtr.Zero, Pack(x, y));
        System.Threading.Thread.Sleep(100);
        PostMessage(h, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, Pack(x, y));
        System.Threading.Thread.Sleep(80);
        PostMessage(h, WM_LBUTTONUP, IntPtr.Zero, Pack(x, y));
        System.Threading.Thread.Sleep(500);
    }

    public static string RectOf(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        return string.Format("({0},{1})-({2},{3}) {4}x{5}", r.L, r.T, r.R, r.B, r.R-r.L, r.B-r.T);
    }
}
"@
[void][Post]::E()

$title = [string][char]0x8BBE + [string][char]0x7F6E
$settingsPath = "$env:APPDATA\TaskbarLyrics\settings.json"

function Get-NavWidth {
    if (-not (Test-Path $settingsPath)) { return -1 }
    (Get-Content $settingsPath -Raw | ConvertFrom-Json).NavWidth
}

$proc = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowTitle -like "*$title*" } | Select-Object -First 1
if (-not $proc) { Write-Host "settings window not running"; exit 1 }

$h = [Post]::ByPid([uint32]$proc.Id, $title)
if ($h -eq [IntPtr]::Zero) { Write-Host "hwnd not found"; exit 1 }

$cr = New-Object Post+RECT
[void][Post]::GetClientRect($h, [ref]$cr)
$scale = $cr.R / 900.0          # client width / 900 DIP
Write-Host ("hwnd {0}   client {1}x{2}   scale {3:N3}" -f $h, $cr.R, $cr.B, $scale)
Write-Host ""

# ---------- test 1: drag the sash ----------
$before = Get-NavWidth
$sashClientX = [int](($before + 2) * $scale)
$rowY = [int](300 * $scale)
Write-Host ("TEST 1  sash drag   NavWidth before = {0}, client press at x={1}" -f $before, $sashClientX)
[Post]::Drag($h, $sashClientX, $rowY, [int](120 * $scale), 20)
$after = Get-NavWidth
Write-Host ("        NavWidth after = {0}   window {1}" -f $after, [Post]::RectOf($h))
if ($after -gt ($before + 60)) { Write-Host "        RESULT: PASS - sash resized the pane" }
else { Write-Host "        RESULT: FAIL - sash did not resize" }

Write-Host ""

# ---------- test 2: caption close button ----------
$visibleBefore = [Post]::IsWindowVisible($h)
$closeClientX = $cr.R - [int](23 * $scale)
$closeY = [int](16 * $scale)
Write-Host ("TEST 2  caption close at client ({0},{1})   visible before = {2}" -f $closeClientX, $closeY, $visibleBefore)
[Post]::Click($h, $closeClientX, $closeY)
Start-Sleep -Milliseconds 800
$visibleAfter = [Post]::IsWindowVisible($h)
Write-Host ("        visible after = {0}" -f $visibleAfter)
if ($visibleBefore -and -not $visibleAfter) { Write-Host "        RESULT: PASS - caption close worked" }
else { Write-Host "        RESULT: FAIL - button did not fire" }
