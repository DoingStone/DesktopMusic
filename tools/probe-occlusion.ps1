$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class OC {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][OC]::E()

$log = Join-Path $env:TEMP "taskbar-lyrics-diag.log"
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Remove-Item $log -ErrorAction SilentlyContinue
$env:TBL_DIAG = "1"
$app = Start-Process -FilePath "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe" -ArgumentList '--tray' -PassThru
Start-Sleep -Seconds 9

$script:ov = [IntPtr]::Zero
[void][OC]::EnumWindows({ param($h,$l)
    $pp=0; [void][OC]::GetWindowThreadProcessId($h,[ref]$pp)
    if($pp -eq $app.Id){
        $t=New-Object Text.StringBuilder 512; [void][OC]::GetWindowText($h,$t,512)
        if($t.ToString() -eq 'TaskbarLyrics Overlay'){ $script:ov = $h }
    }
    return $true
}, [IntPtr]::Zero)
$rect = New-Object OC+RECT; [void][OC]::GetWindowRect($script:ov, [ref]$rect)
$L=[int]$rect.L; $T=[int]$rect.T; $R=[int]$rect.R; $B=[int]$rect.B
$sx = $L + 14; $sy = $T + [int](($B-$T)/2)
Write-Host "overlay=($L,$T)-($R,$B)  sampling ($sx,$sy)"

$bmp = New-Object System.Drawing.Bitmap 1,1
$g = [System.Drawing.Graphics]::FromImage($bmp)
function Bright { $g.CopyFromScreen($sx,$sy,0,0,(New-Object System.Drawing.Size 1,1)); $c=$bmp.GetPixel(0,0); return [int](($c.R+$c.G+$c.B)/3) }
function Report($label) {
    $b = Bright
    $verdict = if ($b -lt 140) { "LYRICS VISIBLE" } else { "COVERED" }
    Write-Host ("  {0,-34} brightness={1,3}  {2}" -f $label, $b, $verdict)
}

Write-Host ""
Write-Host "=== 1) pointer parked away from the taskbar ==="
[void][OC]::SetCursorPos(900, 400); Start-Sleep -Seconds 3
Report "pointer at (900,400)"

Write-Host ""
Write-Host "=== 2) pointer dwelling at the bottom edge (auto-hide reveal) ==="
[void][OC]::SetCursorPos(1250, 1412); Start-Sleep -Seconds 3
Report "pointer parked on taskbar"

Write-Host ""
Write-Host "=== 3) clicking the taskbar 4 times ==="
for ($i=1; $i -le 4; $i++) {
    [OC]::mouse_event(0x02,0,0,0,[IntPtr]::Zero); Start-Sleep -Milliseconds 40
    [OC]::mouse_event(0x04,0,0,0,[IntPtr]::Zero); Start-Sleep -Milliseconds 300
    Report "after click $i"
}
Start-Sleep -Seconds 2
Report "2 s after last click"

Write-Host ""
Write-Host "=== 4) pointer away again ==="
[void][OC]::SetCursorPos(900, 400); Start-Sleep -Seconds 3
Report "pointer back at (900,400)"

$g.Dispose(); $bmp.Dispose()
$env:TBL_DIAG = $null

Write-Host ""
Write-Host "=== app-side owner log ==="
Get-Content $log -ErrorAction SilentlyContinue | Select-String -Pattern 'owner' | Select-Object -First 4
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
