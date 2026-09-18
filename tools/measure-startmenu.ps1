param([int]$Seconds = 10)

# Measures what happens to the overlay when the Start menu is opened, and whether the
# taskbar transiently covers it. Probes with real screen pixels: WindowFromPoint is
# unusable because it honours HTTRANSPARENT and reports the window *below* a
# click-through overlay.
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class SM {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][SM]::E()
Add-Type -AssemblyName System.Drawing

Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$app = Start-Process -FilePath "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe" -ArgumentList '--tray' -PassThru
Start-Sleep -Seconds 9

$script:ov = [IntPtr]::Zero
[void][SM]::EnumWindows({ param($h,$l)
    $pp=0; [void][SM]::GetWindowThreadProcessId($h,[ref]$pp)
    if($pp -eq $app.Id){
        $t=New-Object Text.StringBuilder 512; [void][SM]::GetWindowText($h,$t,512)
        if($t.ToString() -eq 'TaskbarLyrics Overlay'){ $script:ov = $h }
    }
    return $true
},[IntPtr]::Zero)
$rect = New-Object SM+RECT; [void][SM]::GetWindowRect($script:ov,[ref]$rect)
$L=[int]$rect.L; $T=[int]$rect.T; $R=[int]$rect.R; $B=[int]$rect.B
$sx = $L + 14; $sy = $T + [int](($B-$T)/2)
Write-Host "overlay=($L,$T)-($R,$B)  sampling ($sx,$sy)"

$bmp = New-Object System.Drawing.Bitmap 1,1
$g = [System.Drawing.Graphics]::FromImage($bmp)
function Bright { $g.CopyFromScreen($sx,$sy,0,0,(New-Object System.Drawing.Size 1,1)); $c=$bmp.GetPixel(0,0); return [int](($c.R+$c.G+$c.B)/3) }
function Vis { return [SM]::IsWindowVisible($script:ov) }

Write-Host "baseline: visible=$(Vis) brightness=$(Bright)  (dark ~78 = lyrics)"
Write-Host ""
Write-Host "opening the Start menu (Win key)..."
[SM]::keybd_event(0x5B, 0, 0, [IntPtr]::Zero)        # VK_LWIN down
Start-Sleep -Milliseconds 60
[SM]::keybd_event(0x5B, 0, 2, [IntPtr]::Zero)        # VK_LWIN up

for ($i = 0; $i -lt 24; $i++) {
    Start-Sleep -Milliseconds 250
    Write-Host ("  t={0,5} ms  visible={1,-5} brightness={2}" -f (($i+1)*250), (Vis), (Bright))
}

Write-Host ""
Write-Host "closing the Start menu (Esc)..."
[SM]::keybd_event(0x1B, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[SM]::keybd_event(0x1B, 0, 2, [IntPtr]::Zero)
Start-Sleep -Seconds 2
Write-Host "after close: visible=$(Vis) brightness=$(Bright)"

$g.Dispose(); $bmp.Dispose()
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
