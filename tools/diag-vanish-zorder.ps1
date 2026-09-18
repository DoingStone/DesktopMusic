$ErrorActionPreference = 'Continue'
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class ZQ {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][ZQ]::E()
Add-Type -AssemblyName System.Drawing

$exe = "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe"
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$app = Start-Process -FilePath $exe -ArgumentList '--tray' -PassThru

function Find-Overlay($pid2) {
    $script:o = $null
    [void][ZQ]::EnumWindows({ param($h,$l)
        $pp=0; [void][ZQ]::GetWindowThreadProcessId($h,[ref]$pp)
        if($pp -eq $pid2){
            $t=New-Object Text.StringBuilder 512; [void][ZQ]::GetWindowText($h,$t,512)
            if($t.ToString() -eq 'TaskbarLyrics Overlay'){
                $r=New-Object ZQ+RECT; [void][ZQ]::GetWindowRect($h,[ref]$r)
                $script:o = [pscustomobject]@{ H=$h; R=$r }
            }
        }
        return $true
    },[IntPtr]::Zero)
    return $script:o
}

function ZOrderOf($targetH) {
    $h = [ZQ]::GetTopWindow([IntPtr]::Zero)
    $rank = 0
    $overlayRank = -1; $taskbarRank = -1
    while ($h -ne [IntPtr]::Zero -and $rank -lt 600) {
        if ([ZQ]::IsWindowVisible($h)) {
            if ($h -eq $targetH) { $overlayRank = $rank }
            $c = New-Object Text.StringBuilder 128; [void][ZQ]::GetClassName($h,$c,128)
            if ($c.ToString() -eq 'Shell_TrayWnd') { $taskbarRank = $rank }
        }
        $h = [ZQ]::GetWindow($h, 2)
        $rank++
    }
    return @{ Overlay = $overlayRank; Taskbar = $taskbarRank }
}

$o = $null
for ($i=0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 200; $o = Find-Overlay $app.Id; if ($o) { break } }
Start-Sleep -Seconds 4
$o = Find-Overlay $app.Id

$bmp = New-Object System.Drawing.Bitmap 1,1
$g = [System.Drawing.Graphics]::FromImage($bmp)
function Pix($rect) {
    $cx = $rect.L + [int](($rect.R - $rect.L)/2); $cy = $rect.T + [int](($rect.B - $rect.T)/2)
    $g.CopyFromScreen($cx, $cy, 0, 0, (New-Object System.Drawing.Size 1,1))
    $c = $bmp.GetPixel(0,0); return "$($c.R),$($c.G),$($c.B)"
}

$z = ZOrderOf $o.H
Write-Host "=== BEFORE CLICK ==="
Write-Host ("  overlay z-rank={0}  taskbar z-rank={1}  (lower = more on top)" -f $z.Overlay, $z.Taskbar)
Write-Host ("  exstyle=0x{0:X8}" -f [ZQ]::GetWindowLong($o.H,-20))
Write-Host ("  pixel at centre = {0}" -f (Pix $o.R))

$cx = $o.R.L + [int](($o.R.R - $o.R.L)/2); $cy = $o.R.T + [int](($o.R.B - $o.R.T)/2)
[void][ZQ]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 300
[ZQ]::mouse_event(0x02,0,0,0,[IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[ZQ]::mouse_event(0x04,0,0,0,[IntPtr]::Zero)
Start-Sleep -Seconds 2

$o2 = Find-Overlay $app.Id
$z2 = ZOrderOf $o2.H
Write-Host ""
Write-Host "=== AFTER CLICK ==="
Write-Host ("  overlay z-rank={0}  taskbar z-rank={1}" -f $z2.Overlay, $z2.Taskbar)
Write-Host ("  exstyle=0x{0:X8}" -f [ZQ]::GetWindowLong($o2.H,-20))
Write-Host ("  rect=({0},{1})-({2},{3})" -f $o2.R.L,$o2.R.T,$o2.R.R,$o2.R.B)
Write-Host ("  pixel at centre = {0}" -f (Pix $o2.R))
$fg = [ZQ]::GetForegroundWindow()
$fgPid = 0; [void][ZQ]::GetWindowThreadProcessId($fg,[ref]$fgPid)
$fgName = try { (Get-Process -Id $fgPid -ErrorAction Stop).ProcessName } catch { '?' }
Write-Host "  foreground window pid=$fgPid ($fgName)"

Write-Host ""
Write-Host "=== which visible windows overlap the overlay centre? (z-order) ==="
$h = [ZQ]::GetTopWindow([IntPtr]::Zero)
$rank = 0
while ($h -ne [IntPtr]::Zero -and $rank -lt 600) {
    if ([ZQ]::IsWindowVisible($h)) {
        $r = New-Object ZQ+RECT; [void][ZQ]::GetWindowRect($h,[ref]$r)
        if ($cx -ge $r.L -and $cx -le $r.R -and $cy -ge $r.T -and $cy -le $r.B) {
            $c = New-Object Text.StringBuilder 128; [void][ZQ]::GetClassName($h,$c,128)
            $t = New-Object Text.StringBuilder 128; [void][ZQ]::GetWindowText($h,$t,128)
            $pp=0; [void][ZQ]::GetWindowThreadProcessId($h,[ref]$pp)
            $pn = try { (Get-Process -Id $pp -ErrorAction Stop).ProcessName } catch { '?' }
            Write-Host ("  rank={0,-4} {1,-10} class='{2}' title='{3}'" -f $rank, $pn, $c.ToString(), $t.ToString())
        }
    }
    $h = [ZQ]::GetWindow($h, 2)
    $rank++
}

$g.Dispose(); $bmp.Dispose()
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
