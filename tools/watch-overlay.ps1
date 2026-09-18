$ErrorActionPreference = 'Continue'
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class WATCH {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][WATCH]::E()
Add-Type -AssemblyName System.Drawing

$exe = "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe"
$log = Join-Path $env:TEMP "taskbar-lyrics-diag.log"

Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Remove-Item $log -ErrorAction SilentlyContinue
$env:TBL_DIAG = "1"
$app = Start-Process -FilePath $exe -ArgumentList '--tray' -PassThru
Start-Sleep -Seconds 6

function Get-Overlay {
    $script:o = $null
    [void][WATCH]::EnumWindows({ param($h,$l)
        $pp=0; [void][WATCH]::GetWindowThreadProcessId($h,[ref]$pp)
        if($pp -eq $app.Id){
            $t=New-Object Text.StringBuilder 512; [void][WATCH]::GetWindowText($h,$t,512)
            if($t.ToString() -eq 'TaskbarLyrics Overlay'){
                $r=New-Object WATCH+RECT; [void][WATCH]::GetWindowRect($h,[ref]$r)
                $script:o = [pscustomobject]@{ H=$h; R=$r; Vis=[WATCH]::IsWindowVisible($h) }
            }
        }
        return $true
    },[IntPtr]::Zero)
    return $script:o
}

$o = Get-Overlay
if (-not $o) { Write-Host "overlay not found"; exit 1 }
Write-Host "watching overlay visibility for 25 s."
Write-Host ">>> 现在请把鼠标移到任务栏歌词上并点击它（点几次） <<<"
Write-Host ""
Write-Host ("overlay at ({0},{1}) {2}x{3}" -f $o.R.L, $o.R.T, ($o.R.R-$o.R.L), ($o.R.B-$o.R.T))
Write-Host ""

$bmp = New-Object System.Drawing.Bitmap 1,1
$g = [System.Drawing.Graphics]::FromImage($bmp)
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$prevVis = $o.Vis
$prevPix = ''
$events = @()

while ($sw.ElapsedMilliseconds -lt 25000) {
    $cur = Get-Overlay
    if ($cur) {
        $cx = $cur.R.L + [int](($cur.R.R - $cur.R.L)/2)
        $cy = $cur.R.T + [int](($cur.R.B - $cur.R.T)/2)
        $pix = ''
        if ($cx -ge 0 -and $cy -ge 0 -and $cx -lt 2560 -and $cy -lt 1440) {
            $g.CopyFromScreen($cx, $cy, 0, 0, (New-Object System.Drawing.Size 1,1))
            $c = $bmp.GetPixel(0,0)
            $pix = "$($c.R),$($c.G),$($c.B)"
        }
        if ($cur.Vis -ne $prevVis) {
            $events += "t=$($sw.ElapsedMilliseconds)ms  VISIBLE -> $($cur.Vis)"
            Write-Host ("  t={0,6} ms  *** visible={1} ***" -f $sw.ElapsedMilliseconds, $cur.Vis)
            $prevVis = $cur.Vis
        }
        $prevPix = $pix
    } else {
        if ($prevVis -ne 'GONE') {
            $events += "t=$($sw.ElapsedMilliseconds)ms  WINDOW GONE"
            Write-Host ("  t={0,6} ms  *** window GONE ***" -f $sw.ElapsedMilliseconds)
            $prevVis = 'GONE'
        }
    }
    Start-Sleep -Milliseconds 120
}
$g.Dispose(); $bmp.Dispose()

Write-Host ""
Write-Host "visibility changes: $($events.Count)"
$events | ForEach-Object { Write-Host "  $_" }

Write-Host ""
Write-Host "=== app log: hide/show related ==="
Get-Content $log -ErrorAction SilentlyContinue |
    Select-String -Pattern 'Render -> empty|denying|Hide|Show|composite|UNHANDLED' |
    Select-Object -Last 15 | ForEach-Object { Write-Host "  $($_.Line)" }

$env:TBL_DIAG = $null
Write-Host ""
Write-Host "app still running: $([bool](Get-Process -Id $app.Id -ErrorAction SilentlyContinue))"
