$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Text;
using System.Drawing;
using System.Runtime.InteropServices;

public class DragProbe
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }

    public static IntPtr Found = IntPtr.Zero;

    public static IntPtr FindByPid(uint pid, string titlePart) {
        Found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid) {
                var sb = new StringBuilder(512);
                GetWindowText(h, sb, 512);
                if (sb.ToString().Contains(titlePart)) { Found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return Found;
    }

    static int Lum(IntPtr dc, int x, int y) {
        uint px = GetPixel(dc, x, y);
        int r = (int)(px & 0xFF), g = (int)((px >> 8) & 0xFF), b = (int)((px >> 16) & 0xFF);
        return (r + g + b) / 3;
    }

    // Locate the pane/content boundary by the luminance step, not by "darkest pixel":
    // the pane sits near 218, the hairline near 213 and the content near 243, so the first
    // x where the row jumps above 236 is the content edge. Picking a minimum was fragile
    // because anything darker anywhere in the scan range won.
    public static int FindDivider(int left, int right, int y) {
        IntPtr dc = GetDC(IntPtr.Zero);
        int found = -1;
        for (int x = left + 1; x < right; x++) {
            int prev = Lum(dc, x - 1, y);
            int cur = Lum(dc, x, y);
            if (prev <= 232 && cur >= 236) { found = x; break; }
        }
        ReleaseDC(IntPtr.Zero, dc);
        return found;
    }

    public static string Rect(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        return string.Format("({0},{1})-({2},{3}) {4}x{5}", r.L, r.T, r.R, r.B, r.R-r.L, r.B-r.T);
    }

    public static int Tick() { return Environment.TickCount; }
}
"@
[void][DragProbe]::E()

$title = [string][char]0x8BBE + [string][char]0x7F6E   # 设置
$proc = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowTitle -like "*$title*" } | Select-Object -First 1
if (-not $proc) { Write-Host "settings window not running"; exit 1 }

$hwnd = [DragProbe]::FindByPid([uint32]$proc.Id, $title)
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "hwnd not found"; exit 1 }

# Raise it above everything. Screen-pixel sampling then reflects OUR window, which is what
# makes injected input land on it - previously it landed on a full-screen window on top.
# HWND_TOPMOST = -1, SWP_NOSIZE|SWP_NOMOVE|SWP_NOACTIVATE|SWP_SHOWWINDOW = 0x53
[void][DragProbe]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x53)
Start-Sleep -Milliseconds 900

$rc = New-Object DragProbe+RECT
[void][DragProbe]::GetWindowRect($hwnd, [ref]$rc)
$winL = [int]$rc.L; $winT = [int]$rc.T; $winR = [int]$rc.R; $winB = [int]$rc.B
$scale = ($winR - $winL) / 900.0
Write-Host ("window {0}   scale {1:N3}" -f ([DragProbe]::Rect($hwnd)), $scale)

$dc = [DragProbe]::GetDC([IntPtr]::Zero)
$probe = [DragProbe]::GetPixel($dc, $winL + [int](600 * $scale), $winT + [int](400 * $scale))
[void][DragProbe]::ReleaseDC([IntPtr]::Zero, $dc)
$pr = [int]($probe -band 0xFF)
Write-Host ("probe pixel inside window: R={0}  (240+ = our content, so our window is on top)" -f $pr)

$midY = $winT + [int](400 * $scale)
$scanL = $winL + [int](180 * $scale)
$scanR = $winL + [int](300 * $scale)

$divBefore = [DragProbe]::FindDivider($scanL, $scanR, $midY)
$rectBefore = [DragProbe]::Rect($hwnd)
Write-Host ""
Write-Host ("BEFORE  divider x={0}   window {1}" -f $divBefore, $rectBefore)

# Drag the divider 120 px to the right.
$targetDx = [int](120 * $scale)
[void][DragProbe]::SetCursorPos($divBefore, $midY)
Start-Sleep -Milliseconds 400
[DragProbe]::mouse_event(0x02, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 250
for ($i = 1; $i -le 20; $i++) {
    [void][DragProbe]::SetCursorPos(($divBefore + [int]($targetDx * $i / 20.0)), $midY)
    Start-Sleep -Milliseconds 40
}
[DragProbe]::mouse_event(0x04, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 900

$divAfter = [DragProbe]::FindDivider($scanL, ($scanR + $targetDx + 40), $midY)
$rectAfter = [DragProbe]::Rect($hwnd)
Write-Host ("AFTER   divider x={0}   window {1}" -f $divAfter, $rectAfter)
Write-Host ""

$dDiv = $divAfter - $divBefore
$winMoved = ($rectAfter -ne $rectBefore)
Write-Host ("divider moved {0} px   window moved: {1}" -f $dDiv, $winMoved)
if ($dDiv -gt 40) { Write-Host "RESULT: PASS - the sash resized the pane" }
elseif ($winMoved) { Write-Host "RESULT: FAIL - the whole window moved instead" }
else { Write-Host "RESULT: FAIL - nothing happened" }
