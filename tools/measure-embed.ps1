param([int]$Seconds = 12)

# The overlay is now a CHILD of the taskbar, so EnumWindows (top-level only) cannot
# see it. Search the taskbar's children instead, verify the parent link, and probe
# occlusion at high frequency.
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class EMB {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][EMB]::E()
Add-Type -AssemblyName System.Drawing

Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$app = Start-Process -FilePath "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe" -ArgumentList '--tray' -PassThru
Start-Sleep -Seconds 8

$tb = [EMB]::FindWindow("Shell_TrayWnd", $null)
$script:ov = [IntPtr]::Zero
[void][EMB]::EnumChildWindows($tb, { param($h,$l)
    $pp=0; [void][EMB]::GetWindowThreadProcessId($h,[ref]$pp)
    if($pp -eq $app.Id){
        $t=New-Object Text.StringBuilder 512; [void][EMB]::GetWindowText($h,$t,512)
        if($t.ToString() -eq 'TaskbarLyrics Overlay'){ $script:ov = $h }
    }
    return $true
}, [IntPtr]::Zero)

Write-Host "taskbar      = 0x$('{0:X}' -f [int64]$tb)"
Write-Host "overlay child= 0x$('{0:X}' -f [int64]$script:ov)"
if ($script:ov -eq [IntPtr]::Zero) {
    Write-Host "RESULT: overlay is NOT a child of the taskbar (embedding failed)"
} else {
    $par = [EMB]::GetParent($script:ov)
    Write-Host "GetParent    = 0x$('{0:X}' -f [int64]$par)  -> embedded = $($par -eq $tb)"
}

$r = New-Object EMB+RECT; [void][EMB]::GetWindowRect($script:ov, [ref]$r)
$cx = $r.L + [int](($r.R - $r.L)/2); $cy = $r.T + [int](($r.B - $r.T)/2)
Write-Host "rect         = ($($r.L),$($r.T))-($($r.R),$($r.B))  size $($r.R-$r.L)x$($r.B-$r.T)"
Write-Host "visible      = $([EMB]::IsWindowVisible($script:ov))"
Write-Host ""
Write-Host ">>> 请在此期间从别的窗口点击任务栏（就是会闪烁的那个操作）<<<"
Write-Host ""

$prev=$null; $flaps=0; $samples=0
$sw=[System.Diagnostics.Stopwatch]::StartNew()
while ($sw.ElapsedMilliseconds -lt ($Seconds*1000)) {
    $pt = New-Object EMB+POINT; $pt.X=$cx; $pt.Y=$cy
    $hit = [EMB]::WindowFromPoint($pt)
    $owner = if ($hit -eq $script:ov) { 'OVERLAY' } else { 'OTHER' }
    $samples++
    if ($owner -ne $prev) {
        $hpid=0; [void][EMB]::GetWindowThreadProcessId($hit,[ref]$hpid)
        $nm = try { (Get-Process -Id $hpid -ErrorAction Stop).ProcessName } catch { '?' }
        Write-Host ("  t={0,6} ms  {1,-8} hit={2}" -f $sw.ElapsedMilliseconds, $owner, $nm)
        if ($null -ne $prev) { $flaps++ }
        $prev = $owner
    }
    Start-Sleep -Milliseconds 5
}
$sw.Stop()
Write-Host ""
Write-Host "samples=$samples  ownership changes=$flaps"
if ($flaps -eq 0) { Write-Host "RESULT: PASS - overlay never occluded (no flicker)" }
else { Write-Host "RESULT: still flipping ($flaps changes)" }

# Capture so the embedded overlay's appearance can be checked.
$bmp = New-Object System.Drawing.Bitmap ($r.R-$r.L), ($r.B-$r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size ($r.R-$r.L), ($r.B-$r.T)))
$g.Dispose()
$bmp.Save("E:\Qianmory\Desktop\DesktopMusic\artifacts\embedded-overlay.png",[System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved artifacts\embedded-overlay.png"
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
