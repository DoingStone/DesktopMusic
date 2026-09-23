$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$exe = "src\TaskbarLyrics.Cli\bin\Release\net8.0-windows10.0.19041.0\tblc.exe"

# For each song that came out line-level, ask NetEase directly whether it has yrc at all.
# That separates "the data does not exist" from "our resolver picked the wrong candidate".
foreach ($q in @('起风了 买辣椒也用券', '晴天 周杰伦', '夜曲 周杰伦', '稻香 周杰伦', '有何不可 许嵩', '白月光与朱砂痣 大籽')) {
  Write-Host "=== $q ==="
  & $exe yrc $q 2>&1 |
    Select-String -Pattern '选中|真实逐字|逐字总数|RESULT' |
    ForEach-Object { "   $($_.Line.Trim())" }
  Write-Host ""
}
