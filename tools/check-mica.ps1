$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class MICA {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][MICA]::E()

$p = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue
if (-not $p) { Write-Host "app not running"; exit 1 }

$script:h = [IntPtr]::Zero
[void][MICA]::EnumWindows({ param($x,$l)
    $pp=0; [void][MICA]::GetWindowThreadProcessId($x,[ref]$pp)
    if($pp -eq $p.Id){
        $t=New-Object Text.StringBuilder 512; [void][MICA]::GetWindowText($x,$t,512)
        if($t.ToString().Contains([string][char]0x8BBE + [string][char]0x7F6E)){ $script:h = $x }
    }
    return $true
}, [IntPtr]::Zero)
if ($script:h -eq [IntPtr]::Zero) { Write-Host "settings window not found"; exit 1 }

$rect = New-Object MICA+RECT; [void][MICA]::GetWindowRect($script:h, [ref]$rect)
$L=[int]$rect.L; $T=[int]$rect.T; $R=[int]$rect.R; $B=[int]$rect.B
Write-Host "settings window = ($L,$T)-($R,$B)"

# Raise purely by Z-order so a screen capture can see it. Screen pixels are the only
# way to observe a Mica backdrop: PrintWindow captures only what the app itself
# painted, and with sheet-of-glass the app paints nothing there.
# HWND_TOPMOST = -1, SWP_NOSIZE|SWP_NOMOVE|SWP_NOACTIVATE|SWP_SHOWWINDOW = 0x53
[void][MICA]::SetWindowPos($script:h, [IntPtr](-1), 0, 0, 0, 0, 0x0053)
Start-Sleep -Milliseconds 1200

$bmp = New-Object System.Drawing.Bitmap ($R-$L), ($B-$T)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($L, $T, 0, 0, (New-Object System.Drawing.Size ($R-$L), ($B-$T)))
$g.Dispose()
$bmp.Save("E:\Qianmory\Desktop\DesktopMusic\artifacts\mica-check.png", [System.Drawing.Imaging.ImageFormat]::Png)

# Sidebar is the left ~232 DIP (= 290 px at 125%); sample well inside it, below the
# nav pill. The page background is sampled in the card gutter on the right.
function Sample($x, $y, $label) {
    $c = $bmp.GetPixel($x, $y)
    Write-Host ("  {0,-26} ({1,4},{2,4})  R{3,3} G{4,3} B{5,3}" -f $label, $x, $y, $c.R, $c.G, $c.B)
    return $c
}
Write-Host ""
Write-Host "screen pixels:"
$nav1 = Sample 120 640 "sidebar (below nav list)"
$nav2 = Sample 120 200 "sidebar (between items)"
$body = Sample 900 130 "content page bg"
$card = Sample 700 200 "card surface"

Write-Host ""
$dark = ($nav1.R -lt 60 -and $nav1.G -lt 60 -and $nav1.B -lt 60)
if ($dark) {
    Write-Host "VERDICT: sidebar is BLACK -> Mica is NOT rendering (frame extended, no backdrop)"
} else {
    Write-Host "VERDICT: sidebar has colour -> Mica is rendering"
}
$bmp.Dispose()
Write-Host "saved artifacts\mica-check.png"
