<#
  Verifies the settings window's pane toggle sits where it should in both pane states.

  Background: the toggle is pinned to the pane's right edge with an 8 DIP right margin,
  which balances the identity on the left while the pane is wide. Once the pane collapses
  to the 64 DIP rail (SettingsWindow.xaml.cs: RailWidth) the identity is hidden and the
  toggle is the only thing left in the 32 DIP header, so that margin left it visibly right
  of the rail's centre ("收起后没有居中"). ApplyNavCollapsed now centres it, computed from
  RailWidth so the two cannot drift apart.

  Geometry comes from UI Automation (the button's BoundingRectangle), not from screen
  pixels: it is exact, immune to window occlusion, and already in physical pixels.

  Two cases, each with its own isolated settings file (TBL_SETTINGS):
    collapsed -> the toggle is centred in the 64 DIP rail
    expanded  -> the toggle keeps its 8 DIP offset from the pane's right edge
#>
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class RailWin {
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern int GetDpiForSystem();
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr SettingsWindow(uint pid, string overlayTitle) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if (wpid != pid || !IsWindowVisible(h)) return true;
            var cls = new StringBuilder(256); GetClassName(h, cls, 256);
            if (!cls.ToString().StartsWith("HwndWrapper")) return true;
            var t = new StringBuilder(256); GetWindowText(h, t, 256);
            if (t.ToString() == overlayTitle) return true;
            RECT r; GetWindowRect(h, out r);
            if (r.Right - r.Left < 600) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@

[RailWin]::SetProcessDPIAware() | Out-Null
$scale = [RailWin]::GetDpiForSystem() / 96.0

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\TaskbarLyrics.App\bin\Debug\net8.0-windows10.0.19041.0\TaskbarLyrics.exe'
$userSettings = Join-Path $env:APPDATA 'TaskbarLyrics\settings.json'
$railDip = 64.0            # SettingsWindow.xaml.cs: RailWidth
$toggleDip = 28.0          # NavToggleButton Width/Height
$iconDip = 19.0            # the "panel + divider" glyph inside the button
$expandedDip = 232.0       # the default expanded pane width
$marginDip = 8.0           # the toggle's right margin while the pane is wide

$script:passed = 0
$script:failed = 0
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { $script:passed++; Write-Host ("PASS  {0}  {1}" -f $name, $detail) }
    else { $script:failed++; Write-Host ("FAIL  {0}  {1}" -f $name, $detail) }
}

function Start-App([bool]$collapsed) {
    Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700

    $cfg = Get-Content $userSettings -Raw | ConvertFrom-Json
    $cfg.NavCollapsed = $collapsed
    if (-not $collapsed) { $cfg.NavWidth = [int]$expandedDip }
    $path = Join-Path $env:TEMP ("tbl-rail-{0}.json" -f $(if ($collapsed) { 'collapsed' } else { 'expanded' }))
    [System.IO.File]::WriteAllText($path, ($cfg | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
    $env:TBL_SETTINGS = $path
    $env:TBL_DIAG = '1'
    Start-Process -FilePath $exe -ArgumentList '--settings' | Out-Null
    Start-Sleep -Seconds 7
}

function Get-Toggle([bool]$collapsed) {
    $h = [RailWin]::SettingsWindow([uint32]((Get-Process TaskbarLyrics).Id), 'TaskbarLyrics Overlay')
    if ($h -eq [IntPtr]::Zero) { throw 'settings window not found' }
    [void][RailWin]::ShowWindow($h, 9)                      # SW_RESTORE: it starts minimised
    Start-Sleep -Milliseconds 1200                          # a minimised window reports an empty rect

    $win = New-Object RailWin+RECT
    [void][RailWin]::GetWindowRect($h, [ref]$win)

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    if ($null -eq $root) { throw 'no UI Automation root for the settings window' }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavToggleButton')
    $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -eq $el) { throw 'NavToggleButton not found in the automation tree' }
    $rect = $el.Current.BoundingRectangle

    return [pscustomobject]@{
        Hwnd = $h; Left = $win.Left; Top = $win.Top
        Right = $win.Right; Bottom = $win.Bottom
        X = $rect.X; Y = $rect.Y; W = $rect.Width; H = $rect.Height
    }
}

Write-Host ("screen scale {0}  rail {1} DIP = {2:F0} px  toggle {3} DIP = {4:F0} px" -f `
    $scale, $railDip, ($railDip * $scale), $toggleDip, ($toggleDip * $scale))

# ---------------- collapsed: the rail's centre is where the toggle belongs ----------------
Start-App -collapsed $true
$t = Get-Toggle -collapsed $true
$railCentre = $t.Left + ($railDip * $scale) / 2.0
$centre = $t.X + $t.W / 2.0
Write-Host ("collapsed: window left {0}, toggle x {1:F1}..{2:F1} (centre {3:F1}) {4:F1}x{5:F1} px  [rail centre {6:F1}]" -f `
    $t.Left, $t.X, ($t.X + $t.W), $centre, $t.W, $t.H, $railCentre)
Check 'collapsed: the toggle is 28 DIP square' `
    ([Math]::Abs($t.W - $toggleDip * $scale) -le 2 -and [Math]::Abs($t.H - $toggleDip * $scale) -le 2) `
    ("{0:F1}x{1:F1} px, expected {2:F1}x{2:F1}" -f $t.W, $t.H, ($toggleDip * $scale))
Check 'collapsed: the toggle is centred in the rail' `
    ([Math]::Abs($centre - $railCentre) -le 2.0) `
    ("centre {0:F1} px vs rail centre {1:F1} px ({2:F1} px off)" -f $centre, $railCentre, [Math]::Abs($centre - $railCentre))
Check 'collapsed: the 19 DIP glyph clears both rail edges' `
    (($t.X + ($t.W - $iconDip * $scale) / 2.0 -ge $t.Left + 2) -and `
     ($t.X + ($t.W + $iconDip * $scale) / 2.0 -le $t.Left + $railDip * $scale - 2)) `
    ("glyph {0:F1}..{1:F1} px inside the rail 0..{2:F0}" -f `
        ($t.X + ($t.W - $iconDip * $scale) / 2.0), ($t.X + ($t.W + $iconDip * $scale) / 2.0), ($railDip * $scale))
$collapsedCentre = $centre

# ---------------- expanded: the 8 DIP right margin must survive ----------------
Start-App -collapsed $false
$t = Get-Toggle -collapsed $false
$paneRight = $t.Left + $expandedDip * $scale
$expected = $paneRight - $marginDip * $scale - ($toggleDip * $scale) / 2.0
$centre = $t.X + $t.W / 2.0
Write-Host ("expanded:  window left {0}, pane right {1:F0}, toggle x {2:F1}..{3:F1} (centre {4:F1})  [expected centre {5:F1}]" -f `
    $t.Left, $paneRight, $t.X, ($t.X + $t.W), $centre, $expected)
Check 'expanded: the toggle keeps its 8 DIP right margin' `
    ([Math]::Abs(($t.X + $t.W) - ($paneRight - $marginDip * $scale)) -le 2.0) `
    ("right edge {0:F1} px vs {1:F1} px" -f ($t.X + $t.W), ($paneRight - $marginDip * $scale))
Check 'expanded: the centring offset did not leak into the wide pane' `
    ([Math]::Abs($centre - $expected) -le 2.0) `
    ("centre {0:F1} px vs expected {1:F1} px" -f $centre, $expected)
Check 'expanded: the toggle moved away from the rail position' `
    ([Math]::Abs($centre - $collapsedCentre) -gt 100) `
    ("{0:F1} px vs rail {1:F1} px" -f $centre, $collapsedCentre)

Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host ("`n{0} passed, {1} failed" -f $script:passed, $script:failed)
if ($script:failed -gt 0) { exit 1 }
