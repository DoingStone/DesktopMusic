$ErrorActionPreference = 'Continue'
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class DG {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][DG]::E()

$exe = "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe"
$settingsPath = "$env:APPDATA\TaskbarLyrics\settings.json"

# Unlock the overlay by writing the setting directly, so no UI interaction is needed.
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
if (Test-Path $settingsPath) {
    $j = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $j.Locked = $false
    $j | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
    Write-Host "settings: Locked set to false (drag enabled)"
} else {
    Write-Host "no settings file yet; the app will create one - retrying after first run"
}

$app = Start-Process -FilePath $exe -ArgumentList '--tray' -PassThru
Start-Sleep -Seconds 9

# If settings did not exist, unlock now and restart.
if (-not (Test-Path $settingsPath)) {
    Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    $app = Start-Process -FilePath $exe -ArgumentList '--tray' -PassThru
    Start-Sleep -Seconds 8
    Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    $j = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $j.Locked = $false
    $j | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
    $app = Start-Process -FilePath $exe -ArgumentList '--tray' -PassThru
    Start-Sleep -Seconds 9
}

function Find-Overlay($pid2) {
    $script:o = $null
    [void][DG]::EnumWindows({ param($h,$l)
        $pp=0; [void][DG]::GetWindowThreadProcessId($h,[ref]$pp)
        if($pp -eq $pid2){
            $t=New-Object Text.StringBuilder 512; [void][DG]::GetWindowText($h,$t,512)
            if($t.ToString() -eq 'TaskbarLyrics Overlay'){
                $r=New-Object DG+RECT; [void][DG]::GetWindowRect($h,[ref]$r)
                $script:o = [pscustomobject]@{ H=$h; R=$r }
            }
        }
        return $true
    },[IntPtr]::Zero)
    return $script:o
}

$o = Find-Overlay $app.Id
if (-not $o) { Write-Host "FAIL: overlay not found"; exit 1 }
$startLeft = $o.R.L
$startTop = $o.R.T
Write-Host ("overlay before drag: ({0},{1})  exstyle=0x{2:X8}" -f $startLeft, $startTop, [DG]::GetWindowLong($o.H,-20))

$cx = $startLeft + [int](($o.R.R - $o.R.L)/2)
$cy = $startTop + [int](($o.R.B - $o.R.T)/2)
$pt = New-Object DG+POINT; $pt.X=$cx; $pt.Y=$cy
$at = [DG]::WindowFromPoint($pt)
$atPid = 0; [void][DG]::GetWindowThreadProcessId($at,[ref]$atPid)
Write-Host "WindowFromPoint -> pid=$atPid (overlay pid=$($app.Id))  -> $(if($atPid -eq $app.Id){'overlay receives mouse'}else{'NOT receiving mouse'})"

# Drag left by 120 px with several intermediate moves.
Write-Host ""
Write-Host "dragging 120 px to the left..."
[void][DG]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 250
[DG]::mouse_event(0x02,0,0,0,[IntPtr]::Zero)   # left down
Start-Sleep -Milliseconds 120
for ($i = 1; $i -le 8; $i++) {
    [void][DG]::SetCursorPos($cx - (15 * $i), $cy)
    Start-Sleep -Milliseconds 45
}
[DG]::mouse_event(0x04,0,0,0,[IntPtr]::Zero)   # left up
Start-Sleep -Seconds 2

$o2 = Find-Overlay $app.Id
$endLeft = $o2.R.L
Write-Host ("overlay after drag : ({0},{1})" -f $endLeft, $o2.R.T)
$moved = $endLeft - $startLeft
Write-Host ""
if ([Math]::Abs($moved) -gt 40) {
    Write-Host "RESULT: PASS - overlay moved $moved px by dragging"
} else {
    Write-Host "RESULT: FAIL - overlay did not move (delta $moved px)"
}

# Confirm the position was persisted.
Start-Sleep -Seconds 1
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
if (Test-Path $settingsPath) {
    $saved = (Get-Content $settingsPath -Raw | ConvertFrom-Json)
    Write-Host ("saved OffsetX = {0}  (was 0 before drag)" -f $saved.OffsetX)
    if ([Math]::Abs([double]$saved.OffsetX) -gt 20) { Write-Host "RESULT: position persisted" }
    else { Write-Host "NOTE: position not persisted" }
    # Restore lock so the overlay stays click-through for normal use.
    $saved.Locked = $true
    $saved.OffsetX = 0
    $saved | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
    Write-Host "restored Locked=true and OffsetX=0"
}
