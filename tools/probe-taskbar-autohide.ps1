# With the taskbar set to auto-hide, find out whether Shell_TrayWnd actually
# moves off-screen. If it does, our overlay (which tracks its rect) would follow
# it — making the lyrics appear to vanish when the user interacts with the taskbar.
Add-Type @"
using System; using System.Runtime.InteropServices;
public class TBP {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][TBP]::E()
Add-Type -AssemblyName System.Windows.Forms

$tb = [TBP]::FindWindow("Shell_TrayWnd", $null)
$monH = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
Write-Host "screen height = $monH (DPI-aware)"
Write-Host "Shell_TrayWnd = 0x$('{0:X}' -f [int64]$tb)"
Write-Host ""

function Snap([string]$label) {
    $r = New-Object TBP+RECT
    [void][TBP]::GetWindowRect($tb, [ref]$r)
    $onScreen = $r.T -lt $monH - 4
    Write-Host ("  {0,-34} rect=({1},{2})-({3},{4})  h={5}  mostly-on-screen={6}" -f `
        $label, $r.L, $r.T, $r.R, $r.B, ($r.B - $r.T), $onScreen)
}

$orig = New-Object TBP+POINT
[void][TBP]::GetCursorPos([ref]$orig)

Write-Host "=== baseline (cursor parked away from the edge) ==="
[void][TBP]::SetCursorPos(800, 400)
Start-Sleep -Milliseconds 1200
Snap 'cursor at (800,400)'
Start-Sleep -Milliseconds 1200
Snap 'cursor at (800,400) +1.2s'

Write-Host ""
Write-Host "=== cursor pushed to the bottom edge (should reveal an auto-hide bar) ==="
[void][TBP]::SetCursorPos(1000, $monH - 1)
Start-Sleep -Milliseconds 900
Snap 'cursor at bottom edge'
Start-Sleep -Milliseconds 1200
Snap 'cursor at bottom edge +1.2s'

Write-Host ""
Write-Host "=== cursor moved away again ==="
[void][TBP]::SetCursorPos(800, 300)
Start-Sleep -Milliseconds 1500
Snap 'cursor away +1.5s'
Start-Sleep -Milliseconds 1500
Snap 'cursor away +3.0s'

[void][TBP]::SetCursorPos($orig.X, $orig.Y)
