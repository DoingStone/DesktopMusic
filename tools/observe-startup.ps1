$ErrorActionPreference = 'Continue'
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class SEQ {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][SEQ]::E()
Add-Type -AssemblyName System.Drawing

$exe = "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe"
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$app = Start-Process -FilePath $exe -ArgumentList '--tray' -PassThru

function Get-OverlayRect($pid2) {
    $script:r = $null
    [void][SEQ]::EnumWindows({ param($h,$l)
        $pp=0; [void][SEQ]::GetWindowThreadProcessId($h,[ref]$pp)
        if($pp -eq $pid2){
            $t=New-Object Text.StringBuilder 512; [void][SEQ]::GetWindowText($h,$t,512)
            if($t.ToString() -eq 'TaskbarLyrics Overlay'){
                $x=New-Object SEQ+RECT; [void][SEQ]::GetWindowRect($h,[ref]$x); $script:r=$x
            }
        }
        return $true
    },[IntPtr]::Zero)
    return $script:r
}

# Wait for the overlay window to exist at all.
$rect = $null
for ($i = 0; $i -lt 100; $i++) {
    Start-Sleep -Milliseconds 50
    $rect = Get-OverlayRect $app.Id
    if ($rect) { break }
}
if (-not $rect) { Write-Host "overlay window never appeared"; exit 1 }

$w = $rect.R - $rect.L
$h = $rect.B - $rect.T
Write-Host "overlay rect=($($rect.L),$($rect.T)) ${w}x${h}"
Write-Host ""
Write-Host "sampling the overlay region every 100 ms (magenta = the transparency probe):"
Write-Host ""

$bmp = New-Object System.Drawing.Bitmap 1, 1
$g = [System.Drawing.Graphics]::FromImage($bmp)
$samples = @()
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.ElapsedMilliseconds -lt 6000) {
    # Re-read the rect every frame: the window is positioned shortly after it
    # first appears, so a stale rectangle would sample the wrong pixels.
    $cur = Get-OverlayRect $app.Id
    if (-not $cur) { Start-Sleep -Milliseconds 100; continue }
    $cx = $cur.L + [int](($cur.R - $cur.L) / 2)
    $cy = $cur.T + [int](($cur.B - $cur.T) / 2)
    if ($cx -lt 0 -or $cy -lt 0 -or $cx -ge 2560 -or $cy -ge 1440) {
        Start-Sleep -Milliseconds 100
        continue
    }
    $g.CopyFromScreen($cx, $cy, 0, 0, (New-Object System.Drawing.Size 1, 1))
    $c = $bmp.GetPixel(0, 0)
    $magenta = ($c.R -gt 200 -and $c.G -lt 90 -and $c.B -gt 200)
    $samples += [pscustomobject]@{
        T = $sw.ElapsedMilliseconds
        At = "($($cur.L),$($cur.T))"
        RGB = "$($c.R),$($c.G),$($c.B)"
        Magenta = $magenta
    }
    Start-Sleep -Milliseconds 100
}
$g.Dispose(); $bmp.Dispose()

# Collapse consecutive identical results so the output is readable.
$prev = $null
foreach ($s in $samples) {
    $tag = if ($s.Magenta) { '  <<< MAGENTA FLASH' } else { '' }
    if ($prev -ne $s.RGB -or $s.Magenta) {
        Write-Host ("  t={0,5} ms  at={1,-12} RGB={2,-14}{3}" -f $s.T, $s.At, $s.RGB, $tag)
    }
    $prev = $s.RGB
}
$magentaCount = ($samples | Where-Object { $_.Magenta }).Count
Write-Host ""
Write-Host "magenta samples: $magentaCount / $($samples.Count)"
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
