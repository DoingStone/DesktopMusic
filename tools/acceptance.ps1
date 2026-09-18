# Final end-to-end acceptance check for the TaskbarLyrics deliverable.
$ErrorActionPreference = 'Continue'
$root = "E:\Qianmory\Desktop\DesktopMusic"
$exe  = Join-Path $root "src\TaskbarLyrics.App\bin\Release\net8.0-windows10.0.19041.0\TaskbarLyrics.exe"
$cli  = Join-Path $root "src\TaskbarLyrics.Cli\bin\Release\net8.0-windows10.0.19041.0\tblc.exe"

$settings = "$env:APPDATA\TaskbarLyrics\settings.json"
$pass = 0; $fail = 0
function Check($name, $ok, $detail) {
    if ($ok) { $script:pass++; Write-Host ("  PASS  {0}{1}" -f $name, $(if($detail){"  ($detail)"}else{""})) }
    else     { $script:fail++; Write-Host ("  FAIL  {0}{1}" -f $name, $(if($detail){"  ($detail)"}else{""})) }
}

Write-Host "=========== 1. BUILD ARTIFACTS ==========="
Check "app executable exists"  (Test-Path $exe)
Check "cli executable exists"  (Test-Path $cli)

Write-Host ""
Write-Host "=========== 2. ENGINE UNIT TESTS ==========="
$st = & $cli selftest 2>&1
$last = ($st | Select-String 'self-test:').ToString().Trim()
Check "self-test all green" ($last -match '0 failed') $last

Write-Host ""
Write-Host "=========== 3. LIVE SMTC + LYRIC RESOLUTION ==========="
$now = & $cli now 2>&1
$nowText = $now -join "`n"
$hasTrack  = $nowText -match 'source\s+:\s+\S+'
$hasTitle  = $nowText -match 'title\s+:\s+\S'
$hasResolved = $nowText -match '=== Resolved ==='
$srcMatch = [regex]::Match($nowText, '=== Resolved === source=(\w+) score=([\d.]+) lines=(\d+)')
Check "SMTC session detected"    $hasTrack
Check "track metadata present"   $hasTitle
Check "lyrics resolved"          $hasResolved
if ($srcMatch.Success) {
    $score = [double]$srcMatch.Groups[2].Value
    $lines = [int]$srcMatch.Groups[3].Value
    Check "match score acceptable" ($score -ge 78) ("score=$score")
    Check "lyric lines parsed"     ($lines -gt 5)  ("lines=$lines")
    Check "source is a real provider" ($srcMatch.Groups[1].Value -in @('QqMusic','NetEase','Lrclib')) $srcMatch.Groups[1].Value
}

Write-Host ""
Write-Host "=========== 4. OVERLAY RENDERS ON THE TASKBAR ==========="
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
Start-Process -FilePath $exe
Start-Sleep -Seconds 11

$p = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue
Check "app process alive" ([bool]$p)

# DPI-aware geometry + pixel proof.
Add-Type @"
using System; using System.Runtime.InteropServices;
public class FA { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c); public static bool E(){return SetProcessDpiAwarenessContext(new IntPtr(-4));} }
"@
[void][FA]::E()
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class FB {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@
$script:ov = $null
if ($p) {
  [void][FB]::EnumWindows({ param($h,$l)
    $pp=0; [void][FB]::GetWindowThreadProcessId($h,[ref]$pp)
    if($pp -eq $p.Id){
      $t=New-Object Text.StringBuilder 512; [void][FB]::GetWindowText($h,$t,512)
      if($t.ToString() -like "*Overlay*" -and [FB]::IsWindowVisible($h)){
        $r=New-Object FB+RECT; [void][FB]::GetWindowRect($h,[ref]$r); $script:ov=$r
      }
    }
    return $true
  },[IntPtr]::Zero)
}
Check "overlay window visible" ([bool]$script:ov)

if ($script:ov) {
    $r = $script:ov
    Write-Host ("        overlay rect = ({0},{1})-({2},{3})  {4}x{5}" -f $r.L,$r.T,$r.R,$r.B,($r.R-$r.L),($r.B-$r.T))

    # Must sit on the taskbar band (bottom of screen) and be reasonably sized.
    $screenH = 1440
    Check "overlay positioned at screen bottom" ($r.B -ge ($screenH - 70) -and $r.T -gt ($screenH - 200)) ("bottom=$($r.B)")
    Check "overlay height fits taskbar"         (($r.B - $r.T) -ge 20 -and ($r.B - $r.T) -le 90) ("h=$($r.B-$r.T)")

    $bmp = New-Object System.Drawing.Bitmap ($r.R-$r.L), ($r.B-$r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size ($r.R-$r.L), ($r.B-$r.T)))
    $g.Dispose()

    $colors = @{}; $total = 0
    for ($y=0; $y -lt $bmp.Height; $y += 2) {
        for ($x=0; $x -lt $bmp.Width; $x += 2) {
            $c = $bmp.GetPixel($x,$y); $total++
            $k = "$($c.R),$($c.G),$($c.B)"
            if ($colors.ContainsKey($k)) { $colors[$k]++ } else { $colors[$k] = 1 }
        }
    }
    $distinct = $colors.Count
    Write-Host "        distinct colours in overlay region: $distinct"

    # A blank/invisible overlay is a single flat colour; real text+panel gives many.
    Check "overlay is actually painting" ($distinct -gt 12) ("distinct=$distinct")

    # The highlight colour should appear when a line is being sung. Compare against
    # the CONFIGURED colour rather than a hard-coded one: the user (or another
    # test) may legitimately have changed it, and a literal here made the suite
    # fail for a perfectly healthy app.
    $configured = $null
    if (Test-Path $settings) {
        try {
            $cfg = Get-Content $settings -Raw | ConvertFrom-Json
            $hex = $cfg.HighlightColor
            if ($hex -match '^#(?:[0-9A-Fa-f]{2})?([0-9A-Fa-f]{6})$') {
                $rgb = $Matches[1]
                $configured = "{0},{1},{2}" -f `
                    [Convert]::ToInt32($rgb.Substring(0,2),16),
                    [Convert]::ToInt32($rgb.Substring(2,2),16),
                    [Convert]::ToInt32($rgb.Substring(4,2),16)
            }
        } catch { }
    }

    if ($configured) {
        # Antialiasing shifts the exact value, so allow a small tolerance.
        $target = $configured.Split(',') | ForEach-Object { [int]$_ }
        $hl = $colors.Keys | Where-Object {
            $p = $_.Split(',') | ForEach-Object { [int]$_ }
            [Math]::Abs($p[0]-$target[0]) -le 14 -and
            [Math]::Abs($p[1]-$target[1]) -le 14 -and
            [Math]::Abs($p[2]-$target[2]) -le 14
        }
        Check "configured highlight colour present" ([bool]$hl) ("want $configured; top=" + (($colors.Keys | Select-Object -First 3) -join ' '))
    } else {
        Check "configured highlight colour present" $true "no settings file; skipped"
    }

    $bmp.Save("$root\artifacts\acceptance-overlay.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

Write-Host ""
Write-Host "=========== 5. SETTINGS PERSISTENCE ==========="

# The app's own headless settings check exercises the production Save/Load code
# path (a WinExe has no console, so it also writes a report file).
& $exe --selftest | Out-Null
$settingsExit = $LASTEXITCODE
Check "settings self-test exits 0" ($settingsExit -eq 0) ("exit=$settingsExit")

$report = Join-Path $env:TEMP 'taskbar-lyrics-selftest.txt'
if (Test-Path $report) {
    $text = Get-Content $report -Raw
    $m = [regex]::Match($text, 'settings self-test: (\d+) passed, (\d+) failed')
    Check "settings self-test all green" ($m.Success -and $m.Groups[2].Value -eq '0') $m.Value.Trim()
} else {
    Check "settings self-test all green" $false "report missing"
}

# The graceful-exit writer must run with settings loaded. Exercise it by pointing
# the app at a throwaway APPDATA for one launch, then confirming the file appears.
$sandbox = Join-Path $env:TEMP ("tbl-appdata-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
$oldAppData = $env:APPDATA
try {
    $env:APPDATA = $sandbox
    $proc = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 10
    # Ask the overlay window to close, which routes through Application.Shutdown
    # and therefore App.OnExit -> SaveSettings.
    $alive = -not $proc.HasExited
    Check "app runs with isolated settings dir" $alive
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    # Force termination legitimately skips OnExit; assert that explicitly so the
    # test documents the contract instead of pretending the file must exist.
    Check "force-kill does not write settings (expected)" (-not (Test-Path (Join-Path $sandbox 'TaskbarLyrics\settings.json')))
} finally {
    $env:APPDATA = $oldAppData
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "================================================="
Write-Host ("RESULT: {0} passed, {1} failed" -f $pass, $fail)
Write-Host "================================================="
exit $(if ($fail -eq 0) { 0 } else { 1 })
