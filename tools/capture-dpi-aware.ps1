# DPI-aware screen capture.
# An unaware process sees virtualised coordinates (e.g. 2048x1152 on a 2560x1440
# display at 125%), so captures land on the wrong region. Opt into per-monitor v2
# BEFORE touching any screen API so all coordinates are true device pixels.

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class DpiSetup {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
    public static bool Enable() { return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
$ok = [DpiSetup]::Enable()
Write-Host "SetProcessDpiAwarenessContext(PerMonitorV2) = $ok"

Add-Type @"
using System; using System.Runtime.InteropServices;
public class DpiQ {
  [DllImport("user32.dll")] public static extern IntPtr GetThreadDpiAwarenessContext();
  [DllImport("user32.dll")] public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string n);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@
$aw = [DpiQ]::GetAwarenessFromDpiAwarenessContext([DpiQ]::GetThreadDpiAwarenessContext())
Write-Host "thread awareness now = $aw   GetDpiForSystem=$([DpiQ]::GetDpiForSystem())"

Add-Type -AssemblyName System.Drawing

$tb = [DpiQ]::FindWindow("Shell_TrayWnd", $null)
$tbR = New-Object DpiQ+RECT; [void][DpiQ]::GetWindowRect($tb, [ref]$tbR)
Write-Host "Shell_TrayWnd (DPI-aware) = ($($tbR.L),$($tbR.T))-($($tbR.R),$($tbR.B))  $($tbR.R-$tbR.L)x$($tbR.B-$tbR.T)"

# Overlay rect
$p = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue
$script:ov = $null
if ($p) {
  [void][DpiQ]::EnumWindows({ param($h,$l)
    $pp=0; [void][DpiQ]::GetWindowThreadProcessId($h,[ref]$pp)
    if($pp -eq $p.Id){
      $t=New-Object Text.StringBuilder 512; [void][DpiQ]::GetWindowText($h,$t,512)
      if($t.ToString() -like "*Overlay*"){ $r=New-Object DpiQ+RECT; [void][DpiQ]::GetWindowRect($h,[ref]$r); $script:ov=$r }
    }
    return $true
  },[IntPtr]::Zero)
}
if($script:ov){
  $r = $script:ov
  Write-Host "overlay window (DPI-aware) = ($($r.L),$($r.T))-($($r.R),$($r.B))  $($r.R-$r.L)x$($r.B-$r.T)"
}

# Full desktop capture in true device pixels.
$w = [int]([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width)
$h = [int]([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
Write-Host "screen (DPI-aware) = ${w}x${h}"

$full = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($full)
$g.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size $w, $h))
$g.Dispose()
$full.Save("E:\Qianmory\Desktop\DesktopMusic\artifacts\dpi-screen.png", [System.Drawing.Imaging.ImageFormat]::Png)

if($script:ov){
  $r = $script:ov
  $m = 24
  $rx = [Math]::Max(0, $r.L - $m); $ry = [Math]::Max(0, $r.T - $m)
  $rw = ($r.R - $r.L) + 2*$m; $rh = ($r.B - $r.T) + 2*$m
  if($rx+$rw -gt $w){ $rw = $w-$rx }
  if($ry+$rh -gt $h){ $rh = $h-$ry }
  $crop = New-Object System.Drawing.Bitmap $rw, $rh
  $g2 = [System.Drawing.Graphics]::FromImage($crop)
  $g2.DrawImage($full, (New-Object System.Drawing.Rectangle 0,0,$rw,$rh), (New-Object System.Drawing.Rectangle $rx,$ry,$rw,$rh), [System.Drawing.GraphicsUnit]::Pixel)
  $g2.Dispose()

  $big = New-Object System.Drawing.Bitmap ($rw*2), ($rh*2)
  $g3 = [System.Drawing.Graphics]::FromImage($big)
  $g3.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
  $g3.DrawImage($crop, 0, 0, $big.Width, $big.Height)
  $g3.Dispose()
  $big.Save("E:\Qianmory\Desktop\DesktopMusic\artifacts\dpi-overlay-2x.png", [System.Drawing.Imaging.ImageFormat]::Png)

  # Sample a horizontal strip through the overlay centre.
  $cy = [int](($r.T + $r.B)/2)
  $nonBg = 0; $total = 0
  $distinct = @{}
  for($x = $r.L; $x -lt $r.R; $x++){
    $c = $full.GetPixel($x, $cy)
    $key = "$($c.R),$($c.G),$($c.B)"
    if($distinct.ContainsKey($key)){ $distinct[$key]++ } else { $distinct[$key]=1 }
    $total++
    if(-not ($c.R -gt 240 -and $c.G -gt 240 -and $c.B -gt 240)){ $nonBg++ }
  }
  Write-Host "strip y=$cy across overlay: non-white $nonBg/$total"
  Write-Host "top colours on strip:"
  $distinct.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 8 | ForEach-Object {
    Write-Host ("   {0,-16} {1}" -f $_.Key, $_.Value)
  }
  $crop.Dispose(); $big.Dispose()
}
$full.Dispose()
Write-Host "saved artifacts\dpi-screen.png and artifacts\dpi-overlay-2x.png"
