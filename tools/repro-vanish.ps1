$ErrorActionPreference = 'Continue'
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class RP {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][RP]::E()
Add-Type -AssemblyName System.Drawing

$exe = "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe"
$log = Join-Path $env:TEMP "taskbar-lyrics-diag.log"

Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Remove-Item $log -ErrorAction SilentlyContinue
$env:TBL_DIAG = "1"
$app = Start-Process -FilePath $exe -ArgumentList '--tray' -PassThru

function Find-Overlay($pid2) {
    $script:o = $null
    [void][RP]::EnumWindows({ param($h,$l)
        $pp=0; [void][RP]::GetWindowThreadProcessId($h,[ref]$pp)
        if($pp -eq $pid2){
            $t=New-Object Text.StringBuilder 512; [void][RP]::GetWindowText($h,$t,512)
            if($t.ToString() -eq 'TaskbarLyrics Overlay'){
                $r=New-Object RP+RECT; [void][RP]::GetWindowRect($h,[ref]$r)
                $script:o = [pscustomobject]@{ H=$h; R=$r; Vis=[RP]::IsWindowVisible($h) }
            }
        }
        return $true
    },[IntPtr]::Zero)
    return $script:o
}

# Wait for it to appear and settle.
$o = $null
for ($i=0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 200; $o = Find-Overlay $app.Id; if ($o) { break } }
if (-not $o) { Write-Host "overlay never appeared"; exit 1 }
Start-Sleep -Seconds 4
$o = Find-Overlay $app.Id

Write-Host "=== baseline ==="
Write-Host ("  rect=({0},{1})-({2},{3})  visible={4}  exstyle=0x{5:X8}" -f $o.R.L,$o.R.T,$o.R.R,$o.R.B,$o.Vis,[RP]::GetWindowLong($o.H,-20))
Write-Host ("  compositing mode: " + ((Get-Content $log | Select-String 'SourceInitialized mode=' | Select-Object -First 1).Line))
Write-Host ""

$bmp = New-Object System.Drawing.Bitmap 1,1
$g = [System.Drawing.Graphics]::FromImage($bmp)

function Sample-Centre($rect) {
    $cx = $rect.L + [int](($rect.R - $rect.L)/2)
    $cy = $rect.T + [int](($rect.B - $rect.T)/2)
    if ($cx -lt 0 -or $cy -lt 0 -or $cx -ge 2560 -or $cy -ge 1440) { return 'offscreen' }
    $g.CopyFromScreen($cx, $cy, 0, 0, (New-Object System.Drawing.Size 1,1))
    $c = $bmp.GetPixel(0,0)
    return "$($c.R),$($c.G),$($c.B)"
}

Write-Host "=== before click: $(Sample-Centre $o.R) ==="
$cx = $o.R.L + [int](($o.R.R - $o.R.L)/2)
$cy = $o.R.T + [int](($o.R.B - $o.R.T)/2)
$pt = New-Object RP+POINT; $pt.X=$cx; $pt.Y=$cy
$at = [RP]::WindowFromPoint($pt)
$atPid = 0; [void][RP]::GetWindowThreadProcessId($at,[ref]$atPid)
Write-Host "  WindowFromPoint centre -> pid=$atPid (overlay pid=$($app.Id))"
Write-Host ""

Write-Host "=== clicking the overlay 3 times (150 ms apart) ==="
[void][RP]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 250
for ($k=1; $k -le 3; $k++) {
    [RP]::mouse_event(0x02,0,0,0,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [RP]::mouse_event(0x04,0,0,0,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 150
}
Write-Host "clicks sent"
Write-Host ""

for ($i=0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 350
    $cur = Find-Overlay $app.Id
    if (-not $cur) { Write-Host "  t=$([int]($i*350))ms  WINDOW DOES NOT EXIST"; continue }
    $px = Sample-Centre $cur.R
    Write-Host ("  t={0,5}ms vis={1,-5} rect=({2},{3}) px={4}" -f ($i*350), $cur.Vis, $cur.R.L, $cur.R.T, $px)
}

$g.Dispose(); $bmp.Dispose()
Write-Host ""
Write-Host "=== log tail ==="
Get-Content $log | Select-Object -Last 6
$env:TBL_DIAG = $null
Write-Host ""
Write-Host "app alive: $([bool](Get-Process -Id $app.Id -ErrorAction SilentlyContinue))"
