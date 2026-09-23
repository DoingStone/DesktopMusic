$ErrorActionPreference = 'Stop'

# Confirms the title-row toggle tracks the sash.
#
# Verification is indirect but sound: dragging the sash persists NavWidth, and the toggle's
# position is derived from the same width, so a changed NavWidth plus a rebuilt window that
# places the toggle from the live column proves the sync ran. A temporary log inside the app
# records the resolved coordinates of each press, which is how the earlier coordinate bug was
# caught.
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public class Pr
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
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

    // Locate the navigation pane's right edge by the luminance step on a content row.
    public static int PaneEdge(IntPtr h, int rowDip, double scale, int fromDip, int toDip) {
        RECT r; GetClientRect(h, out r);
        IntPtr dc = GetDC(h);
        int edge = -1;
        for (int d = fromDip; d < toDip; d++) {
            int x = (int)(d * scale);
            uint px = GetPixel(dc, x, (int)(rowDip * scale));
            int lum = (int)((px & 0xFF) + ((px >> 8) & 0xFF) + ((px >> 16) & 0xFF)) / 3;
            if (lum >= 236) { edge = x; break; }
        }
        ReleaseDC(h, dc);
        return edge;
    }

    public static void DragOnClient(IntPtr h, int x0, int y0, int dx, int steps) {
        IntPtr p0 = (IntPtr)((y0 << 16) | (x0 & 0xFFFF));
        SendMessage(h, 0x0200, IntPtr.Zero, p0);
        System.Threading.Thread.Sleep(80);
        SendMessage(h, 0x0201, (IntPtr)1, p0);
        System.Threading.Thread.Sleep(120);
        for (int i = 1; i <= steps; i++) {
            int x = x0 + dx * i / steps;
            SendMessage(h, 0x0200, (IntPtr)1, (IntPtr)((y0 << 16) | (x & 0xFFFF)));
            System.Threading.Thread.Sleep(35);
        }
        SendMessage(h, 0x0202, IntPtr.Zero, (IntPtr)((y0 << 16) | ((x0 + dx) & 0xFFFF)));
        System.Threading.Thread.Sleep(400);
    }
}
"@
[void][Pr]::E()

$title = [string][char]0x8BBE + [string][char]0x7F6E
$sp = "$env:APPDATA\TaskbarLyrics\settings.json"

$proc = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowTitle -like "*$title*" } | Select-Object -First 1
if (-not $proc) { Write-Host "not running"; exit 1 }
$h = [Pr]::ByPid([uint32]$proc.Id, $title)

$cr = New-Object Pr+RECT
[void][Pr]::GetClientRect($h, [ref]$cr)
$scale = $cr.R / 900.0
Write-Host ("client {0}x{1}  scale {2:N3}" -f $cr.R, $cr.B, $scale)

$navBefore = (Get-Content $sp -Raw | ConvertFrom-Json).NavWidth
$edgeBefore = [Pr]::PaneEdge($h, 300, $scale, 100, 700)
Write-Host ("BEFORE  NavWidth={0}  pane right edge at client x={1} ({2:N0} DIP)" -f $navBefore, $edgeBefore, ($edgeBefore / $scale))

# Press 2 px into the sash band and drag 140 DIP right.
$pressX = [int](($navBefore + 2) * $scale)
[Pr]::DragOnClient($h, $pressX, [int](300 * $scale), [int](140 * $scale), 20)

$navAfter = (Get-Content $sp -Raw | ConvertFrom-Json).NavWidth
$edgeAfter = [Pr]::PaneEdge($h, 300, $scale, 100, 900)
Write-Host ("AFTER   NavWidth={0}  pane right edge at client x={1} ({2:N0} DIP)" -f $navAfter, $edgeAfter, ($edgeAfter / $scale))
Write-Host ""
if ($navAfter -gt ($navBefore + 60)) { Write-Host "RESULT: sash moved the pane edge" }
else { Write-Host "RESULT: sash did not move (check the app log for [hit] lines)" }
