$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)'

function Get-KugouKrc([string]$keyword, [int]$wantMs) {
  Write-Host "=== $keyword  (期望时长约 $wantMs ms) ==="
  $kw = [uri]::EscapeDataString($keyword)
  $f1 = "$env:TEMP\kg1.json"
  curl.exe -sS --max-time 40 -A $ua -o $f1 "https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=$kw&duration=&hash=" 2>$null
  $j = [System.IO.File]::ReadAllText($f1, [System.Text.Encoding]::UTF8)

  # id and accesskey are JSON strings; duration lets us pick the right edition.
  $cands = [regex]::Matches($j, '"id":"(\d+)"[^}]*?"accesskey":"([^"]+)"[^}]*?"duration":(\d+)')
  if ($cands.Count -eq 0) {
    # order of keys varies, so pair them up field-wise instead
    $ids  = [regex]::Matches($j, '"id":"(\d+)"')      | ForEach-Object { $_.Groups[1].Value }
    $keys = [regex]::Matches($j, '"accesskey":"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    $durs = [regex]::Matches($j, '"duration":(\d+)')  | ForEach-Object { [int]$_.Groups[1].Value }
    $n = [Math]::Min($ids.Count, [Math]::Min($keys.Count, $durs.Count))
    Write-Host "  候选 $n 个（按字段位置配对）"
    if ($n -eq 0) { Write-Host "  无候选"; return }
    $best = 0; $bestDiff = [int]::MaxValue
    for ($i = 0; $i -lt $n; $i++) {
      $d = [Math]::Abs($durs[$i] - $wantMs)
      if ($d -lt $bestDiff) { $bestDiff = $d; $best = $i }
    }
    Write-Host "  选中第 $best 个: duration=$($durs[$best])ms  偏差=$bestDiff ms"
    $id = $ids[$best]; $ak = $keys[$best]
  } else {
    $id = $cands[0].Groups[1].Value; $ak = $cands[0].Groups[2].Value
  }

  $f2 = "$env:TEMP\kg2.json"
  curl.exe -sS --max-time 40 -A $ua -o $f2 "https://lyrics.kugou.com/download?ver=1&client=pc&id=$id&accesskey=$ak&fmt=krc&charset=utf8" 2>$null
  $j2 = [System.IO.File]::ReadAllText($f2, [System.Text.Encoding]::UTF8)
  $b64 = ([regex]::Match($j2, '"content":"([^"]+)"')).Groups[1].Value
  if (-not $b64) { Write-Host "  下载无 content"; return }

  $bytes = [Convert]::FromBase64String($b64)
  Write-Host "  KRC 字节数 $($bytes.Length)  魔数 '$([System.Text.Encoding]::ASCII.GetString($bytes[0..3]))'"

  $key17 = [byte[]]@(0x40,0x47,0x61,0x77,0x5E,0x32,0x74,0x47,0x51,0x36,0x31,0x2D,0xCE,0xD2,0x6E,0x69)
  $body = $bytes[4..($bytes.Length-1)]
  for ($i = 0; $i -lt $body.Length; $i++) { $body[$i] = $body[$i] -bxor $key17[$i % 17] }

  try {
    $ms = New-Object System.IO.MemoryStream(,$body)
    $ms.ReadByte() | Out-Null; $ms.ReadByte() | Out-Null
    $ds = New-Object System.IO.Compression.DeflateStream($ms, [System.IO.Compression.CompressionMode]::Decompress)
    $sr = New-Object System.IO.StreamReader($ds, [System.Text.Encoding]::UTF8)
    $text = $sr.ReadToEnd(); $sr.Close()
    Write-Host "  解压成功，文本 $($text.Length) 字符"

    $lineTags = [regex]::Matches($text, '\[\d+,\d+\]')
    $wordTags = [regex]::Matches($text, '<\d+,\d+,\d+>')
    Write-Host "  行标记 [start,dur]: $($lineTags.Count)    逐字标记 <start,dur,0>: $($wordTags.Count)"
    if ($wordTags.Count -gt 0) {
      $first = ([regex]::Match($text, '^\[\d+,\d+\].*$', 'Multiline')).Value
      Write-Host "  首行结构: $($first.Substring(0, [Math]::Min(80, $first.Length)))"
    }
    $out = "$env:TEMP\sample.krc.txt"
    [System.IO.File]::WriteAllText($out, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  已存 $out"
  } catch {
    Write-Host "  解压失败: $($_.Exception.GetType().Name)"
  }
  Write-Host ""
}

Get-KugouKrc '起风了' 311000
Get-KugouKrc '晴天 周杰伦' 269000
Get-KugouKrc '稻香 周杰伦' 223000
Get-KugouKrc '有何不可 许嵩' 260000
