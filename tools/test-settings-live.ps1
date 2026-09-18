# Drives the settings window via UI Automation to prove edits apply live to the
# taskbar overlay. Compares overlay pixels before and after moving a slider.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class DpiA { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c); public static bool E(){return SetProcessDpiAwarenessContext(new IntPtr(-4));} }
"@
[void][DpiA]::E()

$exe = "E:\Qianmory\Desktop\DesktopMusic\src\TaskbarLyrics.App\bin\Release\net8.0-windows10.0.19041.0\TaskbarLyrics.exe"
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

# Start the app with the settings dialog open.
$app = Start-Process -FilePath $exe -ArgumentList "--settings" -PassThru
Start-Sleep -Seconds 10

function Get-OverlayRect($pid2) {
    Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class OVR {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@
    $script:found = $null
    [void][OVR]::EnumWindows({ param($h,$l)
        $pp=0; [void][OVR]::GetWindowThreadProcessId($h,[ref]$pp)
        if ($pp -eq $pid2 -and [OVR]::IsWindowVisible($h)) {
            $t = New-Object Text.StringBuilder 512; [void][OVR]::GetWindowText($h,$t,512)
            if ($t.ToString() -like "*Overlay*") {
                $r = New-Object OVR+RECT; [void][OVR]::GetWindowRect($h,[ref]$r)
                $script:found = $r
            }
        }
        return $true
    }, [IntPtr]::Zero)
    return $script:found
}

function Get-OverlaySignature($rect) {
    $bmp = New-Object System.Drawing.Bitmap ($rect.R-$rect.L), ($rect.B-$rect.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.L, $rect.T, 0, 0, (New-Object System.Drawing.Size ($rect.R-$rect.L), ($rect.B-$rect.T)))
    $g.Dispose()
    # Row-wise brightness profile: sensitive to text size changes.
    $sb = New-Object System.Text.StringBuilder
    for ($y = 0; $y -lt $bmp.Height; $y += 2) {
        $sum = 0
        for ($x = 0; $x -lt $bmp.Width; $x += 2) {
            $c = $bmp.GetPixel($x, $y)
            $sum += [int](($c.R + $c.G + $c.B) / 3)
        }
        [void]$sb.Append($sum).Append(',')
    }
    $bmp.Dispose()
    return $sb.ToString()
}

$ov = Get-OverlayRect $app.Id
if (-not $ov) { Write-Host "FAIL: overlay window not found"; exit 1 }
Write-Host ("overlay rect = ({0},{1})-({2},{3})" -f $ov.L, $ov.T, $ov.R, $ov.B)

$before = Get-OverlaySignature $ov
Write-Host "captured baseline overlay signature"

# --- find and move the '原文字号' slider via UI Automation ---
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Host "FAIL: settings window not found via UIA"; exit 1 }
Write-Host ("settings window: '{0}'" -f $win.Current.Name)

$sliderCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Slider)
$sliders = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sliderCond)
Write-Host ("found {0} sliders" -f $sliders.Count)

if ($sliders.Count -lt 1) { Write-Host "FAIL: no sliders"; exit 1 }

# First slider in the 外观 tab is 原文字号.
$fontSlider = $sliders.Item(0)
$vp = $fontSlider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
Write-Host ("font-size slider: value={0} min={1} max={2}" -f $vp.Current.Value, $vp.Current.Minimum, $vp.Current.Maximum)

$target = 22.0
$vp.SetValue($target)
Start-Sleep -Seconds 3

Write-Host ("slider set to {0}" -f $vp.Current.Value)
$after = Get-OverlaySignature $ov

if ($before -eq $after) {
    Write-Host "RESULT: FAIL - overlay pixels unchanged, live apply not working"
    exit 1
}

# Quantify how different, to be sure it is a real change and not noise.
$a = $before.Split(',') | Where-Object { $_ -ne '' } | ForEach-Object { [int]$_ }
$b = $after.Split(',')  | Where-Object { $_ -ne '' } | ForEach-Object { [int]$_ }
$n = [Math]::Min($a.Count, $b.Count)
$diff = 0
for ($i = 0; $i -lt $n; $i++) { $diff += [Math]::Abs($a[$i] - $b[$i]) }
$meanDiff = $diff / [Math]::Max(1, $n)

Write-Host ("row-profile mean abs diff = {0:N1} over {1} rows" -f $meanDiff, $n)
if ($meanDiff -gt 50) {
    Write-Host "RESULT: PASS - overlay re-rendered after the settings edit"
    exit 0
} else {
    Write-Host "RESULT: INCONCLUSIVE - change too small to attribute to the edit"
    exit 1
}
