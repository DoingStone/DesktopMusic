param([string]$Mode = "colorkey", [switch]$NoRegion, [switch]$Minimal)

Add-Type @"
using System; using System.Runtime.InteropServices;
public class DpiS { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c); public static bool E(){return SetProcessDpiAwarenessContext(new IntPtr(-4));} }
"@
[void][DpiS]::E()
Add-Type -AssemblyName System.Drawing

Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$env:TBL_COMPOSITE = $Mode
if ($NoRegion) { $env:TBL_NO_REGION = "1" } else { $env:TBL_NO_REGION = $null }
if ($Minimal)  { $env:TBL_MINIMAL  = "1" } else { $env:TBL_MINIMAL  = $null }

Start-Process -FilePath "E:\Qianmory\Desktop\DesktopMusic\src\TaskbarLyrics.App\bin\Release\net8.0-windows10.0.19041.0\TaskbarLyrics.exe"
Start-Sleep -Seconds 11

$env:TBL_COMPOSITE = $null; $env:TBL_NO_REGION = $null; $env:TBL_MINIMAL = $null

# Find overlay rect (DPI-aware).
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class FW {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@
$p = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue
if(-not $p){ Write-Host "[$Mode] APP NOT RUNNING"; exit }
$script:r = $null
[void][FW]::EnumWindows({ param($h,$l)
  $pp=0; [void][FW]::GetWindowThreadProcessId($h,[ref]$pp)
  if($pp -eq $p.Id){
    $t=New-Object Text.StringBuilder 512; [void][FW]::GetWindowText($h,$t,512)
    if($t.ToString() -like "*Overlay*"){ $x=New-Object FW+RECT; [void][FW]::GetWindowRect($h,[ref]$x); $script:r=$x }
  }
  return $true
},[IntPtr]::Zero)

if(-not $script:r){ Write-Host "[$Mode] overlay not found"; exit }
$r = $script:r
$bmp = New-Object System.Drawing.Bitmap ($r.R-$r.L), ($r.B-$r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size ($r.R-$r.L), ($r.B-$r.T)))
$g.Dispose()

# Summarise the overlay region.
$dark=0; $light=0; $mid=0; $mag=0; $total=0
for($y=0;$y -lt $bmp.Height;$y++){
  for($x=0;$x -lt $bmp.Width;$x++){
    $c=$bmp.GetPixel($x,$y); $total++
    $b=[int](($c.R+$c.G+$c.B)/3)
    if($c.R -gt 240 -and $c.G -lt 40 -and $c.B -gt 240){ $mag++ }
    elseif($b -lt 80){ $dark++ }
    elseif($b -gt 200){ $light++ }
    else { $mid++ }
  }
}
$label = "$Mode"
if($NoRegion){ $label += "+noregion" }
if($Minimal){ $label += "+minimal" }
Write-Host ("[{0,-22}] rect=({1},{2})-({3},{4})  dark={5} mid={6} light={7} magenta={8} / {9}" -f `
    $label, $r.L, $r.T, $r.R, $r.B, $dark, $mid, $light, $mag, $total)

$bmp.Save("E:\Qianmory\Desktop\DesktopMusic\artifacts\mode-$label.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
