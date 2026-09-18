$ErrorActionPreference = 'Continue'
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class OP {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][OP]::E()
Add-Type -AssemblyName System.Drawing

$exe = "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe"
$settingsPath = "$env:APPDATA\TaskbarLyrics\settings.json"

function Set-Opacity([double]$value) {
    Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    $j = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $j.OverlayOpacity = $value
    $j.Locked = $true
    $j.OffsetX = 0
    $j | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
    return Start-Process -FilePath $exe -ArgumentList '--tray' -PassThru
}

function Sample-Panel($pid2) {
    $script:r = $null
    [void][OP]::EnumWindows({ param($h,$l)
        $pp=0; [void][OP]::GetWindowThreadProcessId($h,[ref]$pp)
        if($pp -eq $pid2){
            $t=New-Object Text.StringBuilder 512; [void][OP]::GetWindowText($h,$t,512)
            if($t.ToString() -eq 'TaskbarLyrics Overlay'){
                $x=New-Object OP+RECT; [void][OP]::GetWindowRect($h,[ref]$x); $script:r=$x
            }
        }
        return $true
    },[IntPtr]::Zero)
    if(-not $script:r){ return $null }
    # Sample near the left edge of the panel, away from the centred text.
    $px = $script:r.L + 12
    $py = $script:r.T + [int](($script:r.B - $script:r.T)/2)
    $bmp = New-Object System.Drawing.Bitmap 1,1
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($px, $py, 0, 0, (New-Object System.Drawing.Size 1,1))
    $g.Dispose()
    $c = $bmp.GetPixel(0,0); $bmp.Dispose()
    return [pscustomobject]@{ Rect=$script:r; R=$c.R; G=$c.G; B=$c.B }
}

Write-Host "opacity -> measured panel colour (darker = more opaque)"
Write-Host ""
$results = @()
foreach ($value in @(1.0, 0.5, 0.2)) {
    $p = Set-Opacity $value
    Start-Sleep -Seconds 9
    $s = Sample-Panel $p.Id
    if ($s) {
        $brightness = [int](($s.R + $s.G + $s.B)/3)
        Write-Host ("  OverlayOpacity={0,4}  ->  panel RGB=({1},{2},{3})  brightness={4}" -f $value, $s.R, $s.G, $s.B, $brightness)
        $results += [pscustomobject]@{ Opacity=$value; Brightness=$brightness }
    } else {
        Write-Host ("  OverlayOpacity={0,4}  ->  overlay not found" -f $value)
    }
    Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

Write-Host ""
if ($results.Count -eq 3) {
    # More transparent must reveal more of the light taskbar: higher brightness.
    $monotonic = ($results[0].Brightness -lt $results[1].Brightness) -and ($results[1].Brightness -lt $results[2].Brightness)
    if ($monotonic) { Write-Host "RESULT: PASS - opacity control measurably changes the overlay" }
    else { Write-Host "RESULT: FAIL - no monotonic brightness change" }
} else {
    Write-Host "RESULT: INCONCLUSIVE - missing samples"
}

# Restore a sane default.
$j = Get-Content $settingsPath -Raw | ConvertFrom-Json
$j.OverlayOpacity = 1.0
$j | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
Write-Host "restored OverlayOpacity=1.0"
