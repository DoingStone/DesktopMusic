$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Durations here are real values observed during this session, not guesses:
#   呓语 and 我的天空 came from the media session itself (tblc now), 装糊涂 from the TTML
#   document's own <body dur>. Supplying them makes the resolver run its edition check,
#   which the plain sample cannot do because an unknown duration is never a mismatch.
$exe = "src\TaskbarLyrics.Cli\bin\Release\net8.0-windows10.0.19041.0\tblc.exe"

Write-Host "### 正确时长（应与未带时长时一致）"
& $exe coverage "呓语 毛不易@271;我的天空 南征北战@234;装糊涂 许嵩@234" 2>&1 | Select-Object -Last 12

Write-Host ""
Write-Host "### 故意给错的时长（模拟不同版本，应被拒绝 → 无结果）"
& $exe coverage "呓语 毛不易@200;我的天空 南征北战@180" 2>&1 | Select-Object -Last 8
