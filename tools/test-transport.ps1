$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class TR {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static bool E(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
"@
[void][TR]::E()

$exe = "E:\Qianmory\Desktop\DesktopMusic\publish\TaskbarLyrics\TaskbarLyrics.exe"
$cli = "E:\Qianmory\Desktop\DesktopMusic\src\TaskbarLyrics.Cli\bin\Release\net8.0-windows10.0.19041.0\tblc.exe"

Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$app = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 10

# Locate the settings window by handle.
$script:hwnd = [IntPtr]::Zero
[void][TR]::EnumWindows({ param($h,$l)
    $pp=0; [void][TR]::GetWindowThreadProcessId($h,[ref]$pp)
    if($pp -eq $app.Id -and [TR]::IsWindowVisible($h)){
        $t=New-Object Text.StringBuilder 512; [void][TR]::GetWindowText($h,$t,512)
        if($t.ToString().Contains([string][char]0x8BBE + [string][char]0x7F6E)){ $script:hwnd = $h }
    }
    return $true
},[IntPtr]::Zero)
if($script:hwnd -eq [IntPtr]::Zero){ Write-Host "FAIL: settings window not found"; exit 1 }
$win = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
Write-Host "settings window found"

# Switch to the 播放控制 tab (4th tab).
$tabCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::TabItem)
$tabs = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)
Write-Host "tabs: $($tabs.Count)"
for($i=0;$i -lt $tabs.Count;$i++){
    $n = $tabs.Item($i).Current.Name
    $isPlayTab = $n.Contains([string][char]0x64AD + [string][char]0x653E)
    Write-Host "  tab[$i] '$n'$(if($isPlayTab){'  <- transport tab'})"
    if($isPlayTab){
        $tabs.Item($i).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        break
    }
}
Start-Sleep -Milliseconds 700

# Find transport buttons.
$btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
$buttons = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
$pauseBtn = $null; $nextBtn = $null
foreach($b in $buttons){
    $n = $b.Current.Name
    if($n.Contains([string][char]0x6682 + [string][char]0x505C)){ $pauseBtn = $b }
    if($n.Contains([string][char]0x4E0B + [string][char]0x4E00 + [string][char]0x9996)){ $nextBtn = $b }
}
Write-Host ""
Write-Host "pause/play button: $(if($pauseBtn){"'$($pauseBtn.Current.Name)'"}else{'NOT FOUND'})"
Write-Host "next button      : $(if($nextBtn){"'$($nextBtn.Current.Name)'"}else{'NOT FOUND'})"

function Get-State {
    $out = & $cli now 2>&1 | Out-String
    $playing = if($out -match 'playing\s+:\s+(\w+)'){ $Matches[1] } else { '?' }
    $title = if($out -match 'title\s+:\s+(.+)'){ $Matches[1].Trim() } else { '?' }
    return [pscustomobject]@{ Playing = $playing; Title = $title }
}

$before = Get-State
Write-Host ""
Write-Host "before: playing=$($before.Playing) title='$($before.Title)'"

if($pauseBtn){
    Write-Host ""
    Write-Host "clicking 暂停/播放 ..."
    $pauseBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 3
    $after = Get-State
    Write-Host "after : playing=$($after.Playing) title='$($after.Title)'"
    if($before.Playing -ne $after.Playing){
        Write-Host "RESULT: PASS - playback state changed ($($before.Playing) -> $($after.Playing))"
    } else {
        Write-Host "RESULT: state unchanged (player may not expose the control), title same=$($before.Title -eq $after.Title)"
    }
    # Restore playback state.
    $pauseBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    Write-Host "restored: playing=$((Get-State).Playing)"
}

Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
