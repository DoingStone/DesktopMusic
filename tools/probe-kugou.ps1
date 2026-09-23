$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)'

function Try-Kugou([string]$keyword) {
  Write-Host "=== $keyword ==="

  # 1) search -> file hash
  $searchUrl = "https://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword=$([uri]::EscapeDataString($keyword))&page=1&pagesize=5&showtype=1"
  $f1 = "$env:TEMP\kg-search.json"
  curl.exe -sS --max-time 45 -A $ua -o $f1 $searchUrl 2>$null
  if (-not (Test-Path $f1)) { Write-Host "   搜索失败"; return }
  $j1 = [System.IO.File]::ReadAllText($f1, [System.Text.Encoding]::UTF8)
  $hash = ([regex]::Match($j1, '"hash":"([0-9A-Fa-f]{32})"')).Groups[1].Value
  $name = ([regex]::Match($j1, '"songname":"((?:[^"\\]|\\.)*)"')).Groups[1].Value
  $sing = ([regex]::Match($j1, '"singername":"((?:[^"\\]|\\.)*)"')).Groups[1].Value
  if (-not $hash) { Write-Host "   未取到 hash"; return }
  Write-Host "   hash=$hash"
  Write-Host "   曲目长度 $($name.Length) 字符 / 歌手长度 $($sing.Length) 字符"

  # 2) krcs search -> candidate id + accesskey
  $krcsUrl = "https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=$([uri]::EscapeDataString($keyword))&duration=&hash=$hash"
  $f2 = "$env:TEMP\kg-krcs.json"
  curl.exe -sS --max-time 45 -A $ua -o $f2 $krcsUrl 2>$null
  $j2 = [System.IO.File]::ReadAllText($f2, [System.Text.Encoding]::UTF8)
  $id  = ([regex]::Match($j2, '"id":(\d+)')).Groups[1].Value
  $key = ([regex]::Match($j2, '"accesskey":"([^"]+)"')).Groups[1].Value
  if (-not $id) { Write-Host "   krcs 无候选 (status?)"; return }

  # 3) download krc -> base64
  $dlUrl = "https://lyrics.kugou.com/download?ver=1&client=pc&id=$id&accesskey=$key&fmt=krc&charset=utf8"
  $f3 = "$env:TEMP\kg-dl.json"
  curl.exe -sS --max-time 45 -A $ua -o $f3 $dlUrl 2>$null
  $j3 = [System.IO.File]::ReadAllText($f3, [System.Text.Encoding]::UTF8)
  $b64 = ([regex]::Match($j3, '"content":"([^"]+)"')).Groups[1].Value
  if (-not $b64) { Write-Host "   下载无 content"; return }

  $bytes = [Convert]::FromBase64String($b64)
  $magic = [System.Text.Encoding]::ASCII.GetString($bytes[0..3])
  Write-Host "   KRC 字节 $($bytes.Length)  魔数 '$magic'"

  # 4) decrypt: skip 4-byte magic, XOR with the known 17-byte key, then inflate
  $key17 = [byte[]]@(0x40,0x47,0x61,0x77,0x5E,0x32,0x74,0x47,0x51,0x36,0x31,0x2D,0xCE,0xD2,0x6E,0x69)
  $body = $bytes[4..($bytes.Length-1)]
  for ($i = 0; $i -lt $body.Length; $i++) { $body[$i] = $body[$i] -bxor $key17[$i % 17] }

  try {
    $ms = New-Object System.IO.MemoryStream(,$body)
    $ms.ReadByte() | Out-Null; $ms.ReadByte() | Out-Null      # skip zlib header
    $ds = New-Object System.IO.Compression.DeflateStream($ms, [System.IO.Compression.CompressionMode]::Decompress)
    $sr = New-Object System.IO.StreamReader($ds, [System.Text.Encoding]::UTF8)
    $text = $sr.ReadToEnd()
    $sr.Close()
    Write-Host "   解压成功，文本 $($text.Length) 字符"
    $words = [regex]::Matches($text, '\[\d+,\d+\]')
    Write-Host "   逐字标记 [start,dur] 数量: $($words.Count)"
    if ($words.Count -gt 0) {
      $first = ([regex]::Match($text, '^\[\d+,\d+\].*$', 'Multiline')).Value
      Write-Host "   首行前 90 字符: $($first.Substring(0, [Math]::Min(90, $first.Length)))"
    }
  } catch {
    Write-Host "   解压失败: $($_.Exception.GetType().Name)"
  }
  Write-Host ""
}

foreach ($q in @('起风了', '晴天 周杰伦', '稻香 周杰伦', '有何不可 许嵩')) { Try-Kugou $q }
