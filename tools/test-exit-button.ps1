<#
.SYNOPSIS
    End-to-end test of the user's exit path.

.DESCRIPTION
    Launches the app, clicks "([char]0x9000+[char]0x51FA+[char]0x7A0B+[char]0x5E8F)" in the settings window via UI Automation,
    confirms the dialog, and times how long the process takes to disappear.
    This exercises the same ExitApp() the tray menu invokes.
#>
param([int]$TimeoutSec = 30)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = "E:\Qianmory\Desktop\DesktopMusic\src\TaskbarLyrics.App\bin\Release\net8.0-windows10.0.19041.0\TaskbarLyrics.exe"

Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$env:TBL_DIAG = $null
$log = Join-Path $env:TEMP "taskbar-lyrics-diag.log"
Remove-Item $log -ErrorAction SilentlyContinue
$env:TBL_DIAG = "1"

Write-Host "launching app (settings window opens automatically)..."
$app = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 9

if ($app.HasExited) { Write-Host "FAIL: app exited during startup"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)

# The settings window is owned by the overlay, so UIA may not expose it as a
# top-level child. Locate it by its Win32 handle, then wrap that in a UIA element.
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class WH {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@

$script:settingsHwnd = [IntPtr]::Zero
[void][WH]::EnumWindows({ param($h,$l)
    $pp = 0; [void][WH]::GetWindowThreadProcessId($h, [ref]$pp)
    if ($pp -eq $app.Id -and [WH]::IsWindowVisible($h)) {
        $t = New-Object Text.StringBuilder 512; [void][WH]::GetWindowText($h,$t,512)
        if ($t.ToString().Contains([string][char]0x8BBE + [string][char]0x7F6E)) { $script:settingsHwnd = $h }
    }
    return $true
}, [IntPtr]::Zero)

if ($script:settingsHwnd -eq [IntPtr]::Zero) {
    Write-Host "FAIL: settings window handle not found"
    exit 1
}

$win = [System.Windows.Automation.AutomationElement]::FromHandle($script:settingsHwnd)
Write-Host ("settings window: '{0}'" -f $win.Current.Name)

function Find-Button($parent, $nameLike) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($b.Current.Name -like $nameLike) { return $b }
    }
    return $null
}

$quit = Find-Button $win ([string][char]0x9000 + [string][char]0x51FA + [string][char]0x7A0B + [string][char]0x5E8F)
if (-not $quit) { Write-Host "FAIL: ([char]0x9000+[char]0x51FA+[char]0x7A0B+[char]0x5E8F) button not found"; exit 1 }
Write-Host "found ([char]0x9000+[char]0x51FA+[char]0x7A0B+[char]0x5E8F) button"

# Click it; a confirmation dialog follows.
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$quit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 1200

# Confirm the dialog.
$dialog = $null
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) {
    if ($w.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -and
        $w.Current.Name -eq '([char]0x9000+[char]0x51FA+[char]0x7A0B+[char]0x5E8F)') { $dialog = $w; break }
}

if ($dialog) {
    Write-Host "confirmation dialog appeared; clicking OK"
    $ok = Find-Button $dialog ([string][char]0x786E + [string][char]0x5B9A)
    if (-not $ok) { $ok = Find-Button $dialog "OK" }
    if ($ok) { $ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } else { Write-Host "WARN: OK button not found" }
} else {
    Write-Host "WARN: confirmation dialog not detected"
}

# Wait for the process to actually disappear.
$exited = $app.WaitForExit($TimeoutSec * 1000)
$sw.Stop()
$env:TBL_DIAG = $null

Write-Host ""
if ($exited) {
    Write-Host ("RESULT: PASS - process exited {0} ms after clicking ([char]0x9000+[char]0x51FA+[char]0x7A0B+[char]0x5E8F) (exit code {1})" -f `
        $sw.ElapsedMilliseconds, $app.ExitCode)
} else {
    Write-Host ("RESULT: FAIL - process still running after {0} s" -f $TimeoutSec)
}

Write-Host ""
Write-Host "=== exit log ==="
Get-Content $log -ErrorAction SilentlyContinue |
    Select-String -Pattern '\[exit\]|\[tray\]|UNHANDLED' |
    ForEach-Object { Write-Host "  $($_.Line)" }

if (-not $exited) { exit 1 }
exit 0
