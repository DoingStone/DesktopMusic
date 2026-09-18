param([string]$Mode = "alpha")

Add-Type @"
using System; using System.Runtime.InteropServices;
public class DpiZ { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c); public static bool E(){return SetProcessDpiAwarenessContext(new IntPtr(-4));} }
"@
[void][DpiZ]::E()
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class ZQ {
  [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@

Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$env:TBL_COMPOSITE = $Mode
$env:TBL_DIAG = "1"
Start-Process -FilePath "E:\Qianmory\Desktop\DesktopMusic\src\TaskbarLyrics.App\bin\Release\net8.0-windows10.0.19041.0\TaskbarLyrics.exe"
Start-Sleep -Seconds 10
$env:TBL_COMPOSITE = $null; $env:TBL_DIAG = $null

$p = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue
if (-not $p) { Write-Host "APP NOT RUNNING"; exit 1 }
Write-Host "=== mode=$Mode  pid=$($p.Id) ==="

# Walk the real z-order from the top and record ranks.
$h = [ZQ]::GetTopWindow([IntPtr]::Zero)
$rank = 0
$overlayRank = -1; $taskbarRank = -1; $overlayH = [IntPtr]::Zero
$overlayRect = $null
while ($h -ne [IntPtr]::Zero -and $rank -lt 400) {
    $vis = [ZQ]::IsWindowVisible($h)
    $pid2 = 0; [void][ZQ]::GetWindowThreadProcessId($h, [ref]$pid2)
    $cls = New-Object Text.StringBuilder 256; [void][ZQ]::GetClassName($h, $cls, 256)
    $ttl = New-Object Text.StringBuilder 256; [void][ZQ]::GetWindowText($h, $ttl, 256)
    if ($vis) {
        $cn = $cls.ToString()
        if ($pid2 -eq $p.Id -and $ttl.ToString() -like "*Overlay*") {
            $overlayRank = $rank; $overlayH = $h
            $r = New-Object ZQ+RECT; [void][ZQ]::GetWindowRect($h, [ref]$r); $overlayRect = $r
        }
        if ($cn -eq "Shell_TrayWnd") { $taskbarRank = $rank }
    }
    $h = [ZQ]::GetWindow($h, 2)
    $rank++
}
Write-Host "z-order rank (0 = topmost visible): overlay=$overlayRank  taskbar=$taskbarRank"
if ($overlayRect) {
    $r = $overlayRect
    Write-Host "overlay rect = ($($r.L),$($r.T))-($($r.R),$($r.B))  $($r.R-$r.L)x$($r.B-$r.T)"
    $ex = [ZQ]::GetWindowLong($overlayH, -20)
    Write-Host ("overlay exstyle=0x{0:X8}  TOPMOST={1} TRANSPARENT={2} LAYERED={3} NOACTIVATE={4}" -f `
        $ex, [bool]($ex -band 0x8), [bool]($ex -band 0x20), [bool]($ex -band 0x80000), [bool]($ex -band 0x8000000))

    # Sample the overlay region.
    $bmp = New-Object System.Drawing.Bitmap ($r.R-$r.L), ($r.B-$r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size ($r.R-$r.L), ($r.B-$r.T)))
    $g.Dispose()
    $h2 = @{}
    for($y=0;$y -lt $bmp.Height;$y+=2){ for($x=0;$x -lt $bmp.Width;$x+=2){
        $c=$bmp.GetPixel($x,$y); $k="$($c.R),$($c.G),$($c.B)"
        if($h2.ContainsKey($k)){$h2[$k]++}else{$h2[$k]=1}
    }}
    Write-Host "distinct colours in overlay region: $($h2.Count)"
    $h2.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 6 | ForEach-Object { Write-Host "   $($_.Key) x$($_.Value)" }
    $bmp.Save("E:\Qianmory\Desktop\DesktopMusic\artifacts\zq-$Mode.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

Write-Host ""
Write-Host "=== last diag lines ==="
$log = Join-Path $env:TEMP "taskbar-lyrics-diag.log"
Get-Content $log | Select-String -Pattern 'startup|composite|switch|SourceInitialized|taskbar edge' | Select-Object -First 10
