$ErrorActionPreference = "Continue"
$h = @{
  "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"
  "Referer"    = "https://y.qq.com/portal/player.html"
}
Add-Type -AssemblyName System.Web

function Get-LyricInfo {
  param([string]$mid, [string]$label)
  $body = @{ comm = @{ ct = 24; cv = 0 }; req = @{ module = "music.musichallSong.PlayLyricInfo"; method = "GetPlayLyricInfo"; param = @{ songMID = $mid; songID = 0; format = "lrc"; qrc = 0; trans = 1; roma = 1 } } } | ConvertTo-Json -Depth 8 -Compress
  try {
    $jr = (Invoke-WebRequest "https://u.y.qq.com/cgi-bin/musicu.fcg" -Method POST -Body $body -ContentType "application/json" -Headers $h -TimeoutSec 25 -UseBasicParsing).Content | ConvertFrom-Json
    $d = $jr.req.data
    Write-Host ("===== {0}  (mid={1}) =====" -f $label, $mid)
    Write-Host ("hasMultiTrans={0}  transSource={1}  startTs={2}" -f $d.hasMultiTrans, $d.transSource, $d.startTs)

    $ly = ""
    if ($d.lyric) { $ly = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($d.lyric)) }
    $tr = ""
    if ($d.trans) { $tr = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($d.trans)) }

    $lyLines = @($ly -split "`n" | Where-Object { $_ -match '^\[\d' })
    $trLines = @($tr -split "`n" | Where-Object { $_ -match '^\[\d' })
    Write-Host ("lyric timed lines = {0} ; trans timed lines = {1}" -f $lyLines.Count, $trLines.Count)
    Write-Host "--- lyric (timed, first 6) ---"
    $lyLines | Select-Object -First 6 | ForEach-Object { Write-Host "  $_" }
    Write-Host "--- trans (timed, first 6) ---"
    $trLines | Select-Object -First 6 | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
  } catch { Write-Host "[$label] ERR $($_.Exception.Message)" }
}

Get-LyricInfo -mid "0041gObR2QG98x" -label "Shape of You - Ed Sheeran (western, expect trans)"
Get-LyricInfo -mid "000UZM0V3oXVwC" -label "敢爱敢做 - 林子祥 (cantonese/current)"
Get-LyricInfo -mid "0039MnYb0qxYhV" -label "probing another mid"
