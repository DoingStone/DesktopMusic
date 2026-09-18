# Determines what the OS delivers mouse input to across the overlay's area, and
# whether the overlay is actually painted there. Written because injected clicks
# stopped reaching the overlay and the cause had to be measured, not guessed.
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class PROBE {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][PROBE]::E()
Add-Type -AssemblyName System.Drawing

$settingsPath = "$env:APPDATA\TaskbarLyrics\settings.json"
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$dir = Split-Path $settingsPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
if (Test-Path $settingsPath) {
    $j = Get-Content $settingsPath -Raw | ConvertFrom-Json; $j.Locked = $false; $j.OffsetX = 0; $j.OffsetY = 0
    $j | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
} else { '{"Locked": false}' | Set-Content $settingsPath -Encoding UTF8 }

$app = Start-Process -FilePath "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe" -ArgumentList '--tray' -PassThru
Start-Sleep -Seconds 9

$script:ov = [IntPtr]::Zero
[void][PROBE]::EnumWindows({ param($h,$l)
    $pp=0; [void][PROBE]::GetWindowThreadProcessId($h,[ref]$pp)
    if($pp -eq $app.Id){
        $t=New-Object Text.StringBuilder 512; [void][PROBE]::GetWindowText($h,$t,512)
        if($t.ToString() -eq 'TaskbarLyrics Overlay'){ $script:ov = $h }
    }
    return $true
},[IntPtr]::Zero)
$rect = New-Object PROBE+RECT
[void][PROBE]::GetWindowRect($script:ov, [ref]$rect)
# Copy fields into plain ints; reading them straight off the struct in a pipeline
# was returning an array and breaking arithmetic.
$left = [int]$rect.L; $top = [int]$rect.T; $right = [int]$rect.R; $bottom = [int]$rect.B
Write-Host "overlay hwnd=0x$('{0:X}' -f [int64]$script:ov)"
Write-Host "rect=($left,$top)-($right,$bottom)  visible=$([PROBE]::IsWindowVisible($script:ov)) enabled=$([PROBE]::IsWindowEnabled($script:ov))"
Write-Host ""
Write-Host "point             WindowFromPoint            brightness"

$bmp = New-Object System.Drawing.Bitmap 1,1
$g = [System.Drawing.Graphics]::FromImage($bmp)
$y = $top + [int](($bottom - $top)/2)
foreach ($x in @(($left + 20), ($left + 60), ($left + 200), ($left + 287), ($right - 20))) {
    $pt = New-Object PROBE+POINT; $pt.X = $x; $pt.Y = $y
    $hit = [PROBE]::WindowFromPoint($pt)
    $hpid = 0; [void][PROBE]::GetWindowThreadProcessId($hit, [ref]$hpid)
    $nm = try { (Get-Process -Id $hpid -ErrorAction Stop).ProcessName } catch { '?' }
    $cls = New-Object Text.StringBuilder 128; [void][PROBE]::GetClassName($hit, $cls, 128)
    $isOurs = if ($hit -eq $script:ov) { ' <== OURS' } else { '' }
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size 1,1))
    $c = $bmp.GetPixel(0,0); $b = [int](($c.R+$c.G+$c.B)/3)
    Write-Host ("x={0,-5} {1,-12} '{2}'{3}  brightness={4}" -f $x, $nm, $cls.ToString(), $isOurs, $b)
}
$g.Dispose(); $bmp.Dispose()
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$j = Get-Content $settingsPath -Raw | ConvertFrom-Json; $j.Locked = $true; $j.OffsetX = 0; $j.OffsetY = 0
$j | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
