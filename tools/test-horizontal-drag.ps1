# Verifies the drag is horizontal-only: performs a deliberately diagonal drag and
# checks that the horizontal offset changed while the vertical position did not.
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class HDG {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][HDG]::E()

$settingsPath = "$env:APPDATA\TaskbarLyrics\settings.json"
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# Enable dragging. Must work even when no settings file exists yet, otherwise the
# app starts click-through and the drag silently does nothing.
$dir = Split-Path $settingsPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
if (Test-Path $settingsPath) {
    $j = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $j.Locked = $false
    $j.OffsetX = 0; $j.OffsetY = 0
    $j | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
} else {
    '{"Locked": false, "OffsetX": 0, "OffsetY": 0}' | Set-Content $settingsPath -Encoding UTF8
}
Write-Host "interactive (drag enabled) = $((Get-Content $settingsPath -Raw | ConvertFrom-Json).Locked -eq $false)"
$app = Start-Process -FilePath "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe" -ArgumentList '--tray' -PassThru
Start-Sleep -Seconds 9

$script:ov = [IntPtr]::Zero
[void][HDG]::EnumWindows({ param($h,$l)
    $pp=0; [void][HDG]::GetWindowThreadProcessId($h,[ref]$pp)
    if($pp -eq $app.Id){
        $t=New-Object Text.StringBuilder 512; [void][HDG]::GetWindowText($h,$t,512)
        if($t.ToString() -eq 'TaskbarLyrics Overlay'){ $script:ov = $h }
    }
    return $true
},[IntPtr]::Zero)
if ($script:ov -eq [IntPtr]::Zero) { Write-Host "overlay not found"; exit 1 }

function Rect { $r = New-Object HDG+RECT; [void][HDG]::GetWindowRect($script:ov, [ref]$r); return $r }
$before = Rect
# Start near the LEFT of the strip: the centre overlaps the notification area,
# whose tray icons can sit above the overlay and swallow the click.
$cx = $before.L + 40
$cy = $before.T + [int](($before.B - $before.T)/2)
Write-Host "before: ($($before.L),$($before.T))  size $($before.R-$before.L)x$($before.B-$before.T)"

# Deliberately diagonal: +150 px right, +25 px down.
Write-Host "dragging diagonally: +150 px right, +25 px down"
[void][HDG]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 250
[HDG]::mouse_event(0x02,0,0,0,[IntPtr]::Zero)
Start-Sleep -Milliseconds 120
for ($i=1; $i -le 10; $i++) {
    [void][HDG]::SetCursorPos($cx + (15*$i), $cy + [int](2.5*$i))
    Start-Sleep -Milliseconds 40
}
[HDG]::mouse_event(0x04,0,0,0,[IntPtr]::Zero)
Start-Sleep -Seconds 2

$after = Rect
Write-Host "after : ($($after.L),$($after.T))"
$dx = $after.L - $before.L
$dy = $after.T - $before.T
Write-Host ""
Write-Host "horizontal change = $dx px   (expected ~+150)"
Write-Host "vertical   change = $dy px   (expected 0)"

if ([Math]::Abs($dx) -gt 100 -and $dy -eq 0) {
    Write-Host "RESULT: PASS - horizontal-only drag, vertical unchanged"
} elseif ([Math]::Abs($dx) -gt 100 -and [Math]::Abs($dy) -le 2) {
    Write-Host "RESULT: PASS (vertical within rounding: $dy px)"
} else {
    Write-Host "RESULT: FAIL - dx=$dx dy=$dy"
}

Start-Sleep -Seconds 1
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
# Restore defaults so the app is left in a normal state.
if (Test-Path $settingsPath) {
    $j = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $j.Locked = $true; $j.OffsetX = 0; $j.OffsetY = 0
    $j | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
    Write-Host "restored Locked=true, offsets 0"
}
