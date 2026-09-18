param([int]$Seconds = 16)

# Purpose-built flicker probe.
#
# Probe choice matters: WindowFromPoint CANNOT be used, because it honours
# HTTRANSPARENT — our overlay is click-through, so the probe reports the window
# *beneath* it even when the overlay is on top. Sampling the actual screen pixel is
# the only trustworthy signal: our panel is dark (~70-90), the taskbar is light.
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class FP {
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
[void][FP]::E()
Add-Type -AssemblyName System.Drawing

$log = Join-Path $env:TEMP "taskbar-lyrics-diag.log"
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
if (Test-Path $log) { Remove-Item $log -Force }
$env:TBL_DIAG = "1"
$app = Start-Process -FilePath "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe" -ArgumentList '--tray' -PassThru
Start-Sleep -Seconds 8

$script:ov = [IntPtr]::Zero
[void][FP]::EnumWindows({ param($h,$l)
    $pp=0; [void][FP]::GetWindowThreadProcessId($h,[ref]$pp)
    if($pp -eq $app.Id){
        $t=New-Object Text.StringBuilder 512; [void][FP]::GetWindowText($h,$t,512)
        if($t.ToString() -eq 'TaskbarLyrics Overlay'){ $script:ov = $h }
    }
    return $true
},[IntPtr]::Zero)
if ($script:ov -eq [IntPtr]::Zero) { Write-Host "overlay not found"; exit 1 }

$r = New-Object FP+RECT; [void][FP]::GetWindowRect($script:ov, [ref]$r)
# Sample well left of centre, inside the panel but clear of the centred text.
$sx = $r.L + 14
$sy = $r.T + [int](($r.B - $r.T)/2)
Write-Host "overlay=($($r.L),$($r.T))-($($r.R),$($r.B))  sampling pixel ($sx,$sy)"

$bmp = New-Object System.Drawing.Bitmap 1,1
$g = [System.Drawing.Graphics]::FromImage($bmp)
function Brightness { $g.CopyFromScreen($sx,$sy,0,0,(New-Object System.Drawing.Size 1,1)); $c=$bmp.GetPixel(0,0); return [int](($c.R+$c.G+$c.B)/3) }

$baseline = Brightness
Write-Host "baseline brightness = $baseline  (dark => panel visible, light => covered)"
if ($baseline -gt 140) { Write-Host "WARNING: baseline is light; the overlay may not be painting" }
Write-Host ""
Write-Host "cycling desktop-click -> taskbar-click, sampling every 5 ms"
Write-Host ""

function Click($x,$y) {
    [void][FP]::SetCursorPos($x,$y)
    Start-Sleep -Milliseconds 30
    [FP]::mouse_event(0x02,0,0,0,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 25
    [FP]::mouse_event(0x04,0,0,0,[IntPtr]::Zero)
}

$prev=$null; $flaps=0; $samples=0; $coveredMs=0; $maxRun=0; $run=0
$sw=[System.Diagnostics.Stopwatch]::StartNew()
$lastClick=0
while ($sw.ElapsedMilliseconds -lt ($Seconds*1000)) {
    if ($sw.ElapsedMilliseconds - $lastClick -gt 500) {
        $lastClick = $sw.ElapsedMilliseconds
        Click 700 500          # "another page"
        Start-Sleep -Milliseconds 60
        Click 1250 1410        # empty taskbar
    }

    $b = Brightness
    $state = if ($b -lt 140) { 'PANEL' } else { 'COVERED' }
    $samples++

    if ($state -eq 'COVERED') { $coveredMs += 5; $run += 5; if ($run -gt $maxRun) { $maxRun = $run } } else { $run = 0 }

    if ($state -ne $prev) {
        Write-Host ("  t={0,6} ms  {1,-8} brightness={2}" -f $sw.ElapsedMilliseconds, $state, $b)
        if ($null -ne $prev) { $flaps++ }
        $prev = $state
    }
    Start-Sleep -Milliseconds 5
}
$sw.Stop()
$env:TBL_DIAG = $null
$g.Dispose(); $bmp.Dispose()

Write-Host ""
Write-Host "samples=$samples  cover events=$flaps  total covered=${coveredMs}ms  longest single cover=${maxRun}ms"
Write-Host ""
$count = (Get-Content $log -ErrorAction SilentlyContinue | Select-String -Pattern 're-asserting' | Measure-Object).Count
Write-Host "app re-assert count: $count"
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
