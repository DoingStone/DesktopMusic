param([int]$Seconds = 16)

# Forces the flicker condition instead of waiting for a human: repeatedly click the
# desktop (i.e. "another page") and then the taskbar, while probing at ~5 ms whether
# the taskbar has transiently covered the overlay.
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class FRC {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][FRC]::E()
Add-Type -AssemblyName System.Drawing
$script:pbmp = New-Object System.Drawing.Bitmap 1,1
$script:pg = [System.Drawing.Graphics]::FromImage($script:pbmp)
function SamplePixel { $script:pg.CopyFromScreen($cx, $cy, 0, 0, (New-Object System.Drawing.Size 1,1)); $c = $script:pbmp.GetPixel(0,0); return [int](($c.R + $c.G + $c.B)/3) }

$log = Join-Path $env:TEMP "taskbar-lyrics-diag.log"
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
if (Test-Path $log) { Remove-Item $log -Force }
$env:TBL_DIAG = "1"
$app = Start-Process -FilePath "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe" -ArgumentList '--tray' -PassThru
Start-Sleep -Seconds 8

$script:ov = [IntPtr]::Zero
[void][FRC]::EnumWindows({ param($h,$l)
    $pp=0; [void][FRC]::GetWindowThreadProcessId($h,[ref]$pp)
    if($pp -eq $app.Id){
        $t=New-Object Text.StringBuilder 512; [void][FRC]::GetWindowText($h,$t,512)
        if($t.ToString() -eq 'TaskbarLyrics Overlay'){ $script:ov = $h }
    }
    return $true
},[IntPtr]::Zero)
if ($script:ov -eq [IntPtr]::Zero) { Write-Host "overlay not found"; exit 1 }

$r = New-Object FRC+RECT; [void][FRC]::GetWindowRect($script:ov, [ref]$r)
$cx = $r.L + [int](($r.R - $r.L)/2); $cy = $r.T + [int](($r.B - $r.T)/2)
Write-Host "overlay centre = ($cx,$cy)"

# Click target: empty taskbar between the task buttons and the overlay.
$tbClickX = 1250; $tbClickY = 1410
$deskX = 700; $deskY = 500

function Click($x,$y) {
    [void][FRC]::SetCursorPos($x,$y)
    Start-Sleep -Milliseconds 40
    [FRC]::mouse_event(0x02,0,0,0,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 30
    [FRC]::mouse_event(0x04,0,0,0,[IntPtr]::Zero)
}

Write-Host "cycling desktop-click -> taskbar-click while probing occlusion"
Write-Host ""

$prev=$null; $flaps=0; $samples=0; $occludedMs=0
$sw=[System.Diagnostics.Stopwatch]::StartNew()
$lastClick = 0
while ($sw.ElapsedMilliseconds -lt ($Seconds*1000)) {
    # Drive the condition every ~400 ms.
    if ($sw.ElapsedMilliseconds - $lastClick -gt 400) {
        $lastClick = $sw.ElapsedMilliseconds
        Click $deskX $deskY
        Start-Sleep -Milliseconds 80
        Click $tbClickX $tbClickY
    }

    $t0 = $sw.ElapsedMilliseconds
    $pt = New-Object FRC+POINT; $pt.X=$cx; $pt.Y=$cy
    $hit = [FRC]::WindowFromPoint($pt)
    $owner = if ($hit -eq $script:ov) { 'OVERLAY' } else { 'OTHER' }
    $samples++

    if ($owner -eq 'OTHER') { $occludedMs += 5 }
    if ($owner -ne $prev) {
        $hpid=0; [void][FRC]::GetWindowThreadProcessId($hit,[ref]$hpid)
        $nm = try { (Get-Process -Id $hpid -ErrorAction Stop).ProcessName } catch { '?' }
        Write-Host ("  t={0,6} ms  {1,-8} hit={2}" -f $sw.ElapsedMilliseconds, $owner, $nm)
        if ($null -ne $prev) { $flaps++ }
        $prev = $owner
    }
    Start-Sleep -Milliseconds 5
}
$sw.Stop()
$env:TBL_DIAG = $null

Write-Host ""
Write-Host "samples=$samples  ownership changes=$flaps  approx occluded time=${occludedMs}ms"
if ($flaps -eq 0) { Write-Host "RESULT: PASS - lyrics never covered" }
else { Write-Host "RESULT: lyrics were covered $flaps time(s)" }

Write-Host ""
Write-Host "=== z-order decisions log ==="
Get-Content $log -ErrorAction SilentlyContinue | Select-String -Pattern 're-asserting' | Measure-Object | Select-Object -ExpandProperty Count | ForEach-Object { "  re-assert count: $_" }
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
