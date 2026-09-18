$ErrorActionPreference = 'Stop'

# Isolates the Win32 owner API from the WPF app, because the app-level result
# ("SetOwner returned False") did not show WHY. Creates real top-level windows with the
# same extended styles the overlay uses and reports what the API actually does.
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class OwnerProbe {
    [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll", SetLastError=true)]
    public static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll", SetLastError=true)]
    public static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError=true)]
    public static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int value);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string cls, string name);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    public static int LastError() { return Marshal.GetLastWin32Error(); }
    public static void ClearError() { Marshal.GetLastWin32Error(); }
}
"@

[void][OwnerProbe]::SetProcessDpiAwarenessContext([IntPtr](-4))

$taskbar = [OwnerProbe]::FindWindowW("Shell_TrayWnd", $null)
Write-Host "Shell_TrayWnd = 0x$('{0:X}' -f $taskbar.ToInt64())"
if ($taskbar -eq [IntPtr]::Zero) { Write-Host "taskbar not found"; exit 1 }
Write-Host ""

# Extended styles matching the overlay: TOPMOST | TOOLWINDOW | NOACTIVATE | LAYERED
$styles = @(
    @{ Name = "plain top-level";        Ex = 0x00000000 },
    @{ Name = "TOOLWINDOW";             Ex = 0x00000080 },
    @{ Name = "TOPMOST|TOOLWINDOW";     Ex = 0x00000088 },
    @{ Name = "overlay-like (+NOACTIVATE+LAYERED)"; Ex = 0x080800A8 }
)

# Written as decimal: PowerShell parses 0x80000000 as a negative Int32 and refuses the
# UInt32 cast.
$WS_POPUP   = [uint32]2147483648
$WS_VISIBLE = [uint32]268435456

foreach ($s in $styles) {
    [OwnerProbe]::ClearError()
    $h = [OwnerProbe]::CreateWindowExW([uint32]$s.Ex, "STATIC", "owner probe", ($WS_POPUP -bor $WS_VISIBLE),
                                       100, 100, 200, 60, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero)
    if ($h -eq [IntPtr]::Zero) { Write-Host "$($s.Name): CreateWindowEx failed ($([OwnerProbe]::LastError()))"; continue }

    [OwnerProbe]::ClearError()
    $prev = [OwnerProbe]::SetWindowLongPtrW($h, -8, $taskbar)
    $err  = [OwnerProbe]::LastError()

    $viaGetWindow  = [OwnerProbe]::GetWindow($h, 4)          # GW_OWNER
    $viaGetLongPtr = [OwnerProbe]::GetWindowLongPtrW($h, -8)

    Write-Host ("{0}" -f $s.Name)
    Write-Host ("   SetWindowLongPtr -> prev=0x{0:X}  GetLastError={1}" -f $prev.ToInt64(), $err)
    Write-Host ("   GetWindow(GW_OWNER)      = 0x{0:X}  {1}" -f $viaGetWindow.ToInt64(), $(if($viaGetWindow -eq $taskbar){"MATCH"}else{"no"}))
    Write-Host ("   GetWindowLongPtr(-8)     = 0x{0:X}  {1}" -f $viaGetLongPtr.ToInt64(), $(if($viaGetLongPtr -eq $taskbar){"MATCH"}else{"no"}))
    Write-Host ""

    [void][OwnerProbe]::DestroyWindow($h)
}
