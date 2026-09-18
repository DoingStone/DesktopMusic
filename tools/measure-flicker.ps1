param([int]$Seconds = 12)

# Fast occlusion probe: WindowFromPoint at the overlay's centre is O(1), so this can
# sample every few milliseconds and actually catch a brief flicker. The earlier
# z-order-ranking approach walked up to 900 windows per probe and was far too slow.
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class OCC {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][OCC]::E()

$log = Join-Path $env:TEMP "taskbar-lyrics-diag.log"
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
if (Test-Path $log) { Remove-Item $log -Force }
$env:TBL_DIAG = "1"
$app = Start-Process -FilePath "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe" -ArgumentList '--tray' -PassThru
Start-Sleep -Seconds 7

$script:ov = [IntPtr]::Zero
[void][OCC]::EnumWindows({ param($h,$l)
    $pp=0; [void][OCC]::GetWindowThreadProcessId($h,[ref]$pp)
    if($pp -eq $app.Id){
        $t=New-Object Text.StringBuilder 512; [void][OCC]::GetWindowText($h,$t,512)
        if($t.ToString() -eq 'TaskbarLyrics Overlay'){ $script:ov = $h }
    }
    return $true
},[IntPtr]::Zero)
if ($script:ov -eq [IntPtr]::Zero) { Write-Host "overlay not found"; exit 1 }

$r = New-Object OCC+RECT; [void][OCC]::GetWindowRect($script:ov, [ref]$r)
$cx = $r.L + [int](($r.R - $r.L)/2); $cy = $r.T + [int](($r.B - $r.T)/2)
Write-Host "overlay=0x$('{0:X}' -f [int64]$script:ov) centre=($cx,$cy)"
Write-Host ""
Write-Host "sampling who owns the overlay's centre every ~5 ms for $Seconds s."
Write-Host ">>> 请在此期间从别的窗口点击任务栏（就是会闪烁的那个操作）<<<"
Write-Host ""

$prev = $null
$flaps = 0
$samples = 0
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.ElapsedMilliseconds -lt ($Seconds * 1000)) {
    $pt = New-Object OCC+POINT; $pt.X = $cx; $pt.Y = $cy
    $hit = [OCC]::WindowFromPoint($pt)
    $root = [OCC]::GetAncestor($hit, 2)
    $owner = if ($root -eq $script:ov -or $hit -eq $script:ov) { 'OVERLAY' } else { 'OTHER' }
    $samples++

    if ($owner -ne $prev) {
        $hpid = 0; [void][OCC]::GetWindowThreadProcessId($hit, [ref]$hpid)
        $name = try { (Get-Process -Id $hpid -ErrorAction Stop).ProcessName } catch { '?' }
        Write-Host ("  t={0,6} ms  {1,-8} hit={2}" -f $sw.ElapsedMilliseconds, $owner, $name)
        if ($null -ne $prev) { $flaps++ }
        $prev = $owner
    }
    Start-Sleep -Milliseconds 5
}
$sw.Stop()
$env:TBL_DIAG = $null

Write-Host ""
Write-Host "samples=$samples  ownership changes=$flaps"
if ($flaps -eq 0) { Write-Host "=> overlay owned its centre throughout: NOT occluded" }
else { Write-Host "=> ownership flipped $flaps time(s): the taskbar transiently covers us" }

Write-Host ""
Write-Host "=== app z-order decisions ==="
Get-Content $log -ErrorAction SilentlyContinue | Select-String -Pattern 'climbed|re-asserting' | Select-Object -First 8
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
