param([int]$Seconds = 12)

Add-Type @"
using System; using System.Runtime.InteropServices;
public class DpiS { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c); public static bool E(){return SetProcessDpiAwarenessContext(new IntPtr(-4));} }
"@
[void][DpiS]::E()

# Measure the two things that decide lyric sync accuracy:
#   1. does the position advance at exactly wall-clock rate?
#   2. how stale is the position when we read it (Position vs LastUpdatedTime)?
#   3. what playback rate does the player report?
$log = Join-Path $env:TEMP "taskbar-lyrics-diag.log"
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
if (Test-Path $log) { Remove-Item $log -Force }

# Launch with diagnostics so the app's own position handling is observable too.
$env:TBL_DIAG = "1"
$app = Start-Process -FilePath "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe" -ArgumentList '--tray' -PassThru
Start-Sleep -Seconds 6

$cli = "E:\Qianmory\Desktop\DesktopMusic\src\TaskbarLyrics.Cli\bin\Release\net8.0-windows10.0.19041.0\tblc.exe"
Write-Host "=== sampling SMTC position every 500 ms ==="
Write-Host ("{0,-10} {1,-14} {2}" -f 'wall(ms)', 'pos(ms)', 'delta')

$prevWall = $null; $prevPos = $null
$samples = @()
for ($i = 0; $i -lt ($Seconds * 2); $i++) {
    $out = & $cli now 2>&1 | Out-String
    $posMatch = [regex]::Match($out, 'position\s+:\s+(\d+):(\d+):(\d+)\.(\d+)')
    if ($posMatch.Success) {
        $posMs = (( [int]$posMatch.Groups[1].Value * 3600 + [int]$posMatch.Groups[2].Value * 60 + [int]$posMatch.Groups[3].Value) * 1000) + [int]$posMatch.Groups[4].Value
        $wall = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
        if ($prevWall -ne $null) {
            $dWall = $wall - $prevWall
            $dPos  = $posMs - $prevPos
            $ratio = if ($dWall -gt 0) { [math]::Round($dPos / $dWall, 3) } else { 0 }
            Write-Host ("{0,-10} {1,-14} pos+{2,-6} wall+{3,-6} ratio={4}" -f ($wall % 100000), $posMs, $dPos, $dWall, $ratio)
            $samples += [pscustomobject]@{ dPos=$dPos; dWall=$dWall; ratio=$ratio }
        }
        $prevWall = $wall; $prevPos = $posMs
    }
    Start-Sleep -Milliseconds 500
}
$env:TBL_DIAG = $null
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force

if ($samples.Count -gt 3) {
    $sumPos = ($samples | Measure-Object -Property dPos -Sum).Sum
    $sumWall = ($samples | Measure-Object -Property dWall -Sum).Sum
    Write-Host ""
    Write-Host ("total: pos advanced {0} ms over {1} ms wall  ->  rate = {2}" -f $sumPos, $sumWall, [math]::Round($sumPos/$sumWall,4))
    Write-Host "(rate 1.000 means the reported position tracks real time; anything else causes progressive drift)"
}
