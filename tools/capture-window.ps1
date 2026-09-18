param(
    [string]$TitleLike = "\u8BBE\u7F6E",
    [string]$Out = "E:\Qianmory\Desktop\DesktopMusic\artifacts\window-capture.png"
)

# Captures a window by rendering it directly with PrintWindow, so the result does not
# depend on which window happens to be in front. Needed because SetForegroundWindow
# is refused for background processes, which made screen-region captures useless.
Add-Type @"
using System; using System.Text; using System.Drawing; using System.Runtime.InteropServices;
public class PW {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][PW]::E()
Add-Type -AssemblyName System.Drawing

$p = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue
if (-not $p) { Write-Host "app not running"; exit 1 }

$needle = [regex]::Unescape($TitleLike)
$script:h = [IntPtr]::Zero
[void][PW]::EnumWindows({ param($x,$l)
    $pp=0; [void][PW]::GetWindowThreadProcessId($x,[ref]$pp)
    if($pp -eq $p.Id){
        $t=New-Object Text.StringBuilder 512; [void][PW]::GetWindowText($x,$t,512)
        if($t.ToString().Contains($needle)){ $script:h = $x }
    }
    return $true
}, [IntPtr]::Zero)

if ($script:h -eq [IntPtr]::Zero) { Write-Host "window not found"; exit 1 }

$rect = New-Object PW+RECT
[void][PW]::GetWindowRect($script:h, [ref]$rect)
$L=[int]$rect.L; $T=[int]$rect.T; $R=[int]$rect.R; $B=[int]$rect.B
$w = $R-$L; $h2 = $B-$T
Write-Host "window rect = ($L,$T)-($R,$B)  ${w}x${h2}"

$bmp = New-Object System.Drawing.Bitmap $w, $h2
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# PW_RENDERFULLCONTENT = 2, required for composited (DWM) windows.
$ok = [PW]::PrintWindow($script:h, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
Write-Host "PrintWindow ok = $ok"
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)

Write-Host ""
Write-Host "colour samples:"
foreach ($pt in @(@(8, [int]($h2/2)), @(12, 300), @([int]($w/2), 120), @([int]($w/2), 60))) {
    $c = $bmp.GetPixel($pt[0], $pt[1])
    Write-Host ("  ({0,4},{1,4})  R{2,3} G{3,3} B{4,3}" -f $pt[0], $pt[1], $c.R, $c.G, $c.B)
}
$bmp.Dispose()
Write-Host "saved $Out"
