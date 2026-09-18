# Measures launch -> process-gone with diagnostics DISABLED, so per-frame logging
# cannot distort the numbers. TBL_AUTOEXIT fires the real ExitApp() path.
param([int]$AutoExitMs = 5000, [int]$Runs = 3)

$exe = "E:\Qianmory\Desktop\DesktopMusic\src\TaskbarLyrics.App\bin\Release\net8.0-windows10.0.19041.0\TaskbarLyrics.exe"

Write-Host "Measuring $Runs runs, auto-exit at ${AutoExitMs} ms, diagnostics OFF"
Write-Host ""
Write-Host ("{0,-6} {1,-14} {2,-14} {3}" -f 'run', 'total(ms)', 'shutdown(ms)', 'exitCode')

$totals = @()
for ($i = 1; $i -le $Runs; $i++) {
    Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2

    $env:TBL_DIAG = $null
    $env:TBL_AUTOEXIT = "$AutoExitMs"

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $exe -PassThru
    $ok = $p.WaitForExit(30000)
    $sw.Stop()
    $total = $sw.ElapsedMilliseconds

    $env:TBL_AUTOEXIT = $null

    if (-not $ok) {
        Write-Host ("{0,-6} {1,-14} {2,-14} {3}" -f $i, 'TIMEOUT', '-', '-')
        continue
    }

    $shutdown = $total - $AutoExitMs
    $totals += $shutdown
    Write-Host ("{0,-6} {1,-14} {2,-14} {3}" -f $i, $total, $shutdown, $p.ExitCode)
}

if ($totals.Count -gt 0) {
    Write-Host ""
    Write-Host ("shutdown time: min={0} max={1} avg={2} ms" -f `
        ($totals | Measure-Object -Minimum).Minimum,
        ($totals | Measure-Object -Maximum).Maximum,
        [math]::Round(($totals | Measure-Object -Average).Average, 0))
}
