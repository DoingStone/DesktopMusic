using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TaskbarLyrics.App.Interop;

/// <summary>Native helpers for positioning and styling the taskbar overlay.</summary>
public static class NativeMethods
{
    // ---- window styles -------------------------------------------------
    internal const int GWL_EXSTYLE = -20;

    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    internal const int WS_EX_LAYERED = 0x00080000;

    // ---- set-window-pos flags -----------------------------------------
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>Keep the window above normal windows without stealing focus.</summary>
    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    // ---- monitor / DPI -------------------------------------------------
    internal const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
        string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType,
        out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Apply overlay window styles (no activate, topmost, tool window).</summary>
    internal static void ApplyOverlayStyles(Window window, bool clickThrough)
    {
        // Diagnostic escape hatch: setting extended styles on an already-created
        // layered (AllowsTransparency) window can disturb its composition.
        if (Environment.GetEnvironmentVariable("TBL_NO_STYLES") == "1") return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;

        // WS_EX_TRANSPARENT makes the whole window ignore mouse input, letting
        // clicks fall through to the taskbar underneath.
        if (clickThrough) exStyle |= WS_EX_TRANSPARENT;
        else exStyle &= ~WS_EX_TRANSPARENT;

        SetWindowLong(handle, GWL_EXSTYLE, exStyle);
    }

    internal static void SetTopmost(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Place <paramref name="window"/> directly <b>above</b>
    /// <paramref name="below"/> in the Z-order.
    /// <para>
    /// <c>SetWindowPos</c>'s <c>hWndInsertAfter</c> argument names the window that
    /// should end up <i>above</i> the one being moved — not the window to sit
    /// beneath. Passing the neighbour itself therefore buries the window under it,
    /// which is the opposite of the intent; that mistake let the taskbar paint over
    /// the overlay. The correct insert-after target is whatever currently sits
    /// above the neighbour.
    /// </para>
    /// </summary>
    internal static void SetAbove(Window window, IntPtr below)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        // If nothing is above `below`, it is at the very top: go to the topmost band.
        var insertAfter = below == IntPtr.Zero ? IntPtr.Zero : GetWindow(below, GW_HWNDPREV);

        if (insertAfter == IntPtr.Zero)
        {
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            return;
        }

        SetWindowPos(handle, insertAfter, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private const uint GW_HWNDPREV = 3;

    private const uint GW_OWNER = 4;

    /// <summary>Index of a top-level window's owner.</summary>
    private const int GWLP_HWNDPARENT = -8;

    /// <summary>
    /// Make <paramref name="owner"/> the owner of <paramref name="window"/>.
    /// <para>
    /// Windows guarantees an owned window is always above its owner, so with the
    /// taskbar as owner the overlay cannot be painted over when Explorer re-raises
    /// <c>Shell_TrayWnd</c> — the mechanism behind both the flicker and the lyrics
    /// vanishing while the Start menu is open.
    /// </para>
    /// <para>
    /// This sets the <b>owner</b>, not the parent, so the window stays top-level and
    /// WPF's coordinate handling is unaffected. (Reparenting a WPF window with
    /// <c>SetParent</c> puts it thousands of pixels off screen — verified earlier.)
    /// </para>
    /// </summary>
    internal static bool SetOwner(IntPtr window, IntPtr owner, out string detail)
    {
        detail = "not attempted";

        if (window == IntPtr.Zero || owner == IntPtr.Zero)
        {
            detail = $"invalid handles window=0x{window.ToInt64():X} owner=0x{owner.ToInt64():X}";
            return false;
        }

        var existing = GetWindowOwner(window);
        if (existing == owner)
        {
            detail = "already owned";
            return true;
        }

        SetWindowLongPtr(window, GWLP_HWNDPARENT, owner);
        int err = Marshal.GetLastWin32Error();

        var after = GetWindowOwner(window);
        detail = $"prev=0x{existing.ToInt64():X} winerr={err} after=0x{after.ToInt64():X}";
        return after == owner;
    }

    /// <summary>Drop the owner relationship.</summary>
    internal static void ClearOwner(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        SetWindowLongPtr(window, GWLP_HWNDPARENT, IntPtr.Zero);
    }

    /// <summary>
    /// Read a top-level window's owner, via the documented <c>GetWindow(GW_OWNER)</c>
    /// query rather than reading GWLP_HWNDPARENT back.
    /// </summary>
    internal static IntPtr GetWindowOwner(IntPtr window) =>
        window == IntPtr.Zero ? IntPtr.Zero : GetWindow(window, GW_OWNER);

    // SetWindowLongPtr exists only on 64-bit Windows; 32-bit builds fall back to
    // SetWindowLong. Both take a pointer-sized value at GWLP_HWNDPARENT.
    private static void SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, index, value);
        else SetWindowLong32(hWnd, index, value.ToInt32());
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>Clear the WS_EX_TOPMOST flag from a window (for settings dialog). </summary>
    internal static void ClearTopmost(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        exStyle &= ~WS_EX_TOPMOST;
        SetWindowLong(handle, GWL_EXSTYLE, exStyle);
    }

    internal static RECT? GetWindowBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        return GetWindowRect(hwnd, out var rect) ? rect : null;
    }

    private const uint GW_HWNDNEXT = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    /// <summary>True when the window carries <c>WS_EX_TOPMOST</c>.</summary>
    internal static bool IsTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        return (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> sits above <paramref name="reference"/>
    /// in the Z-order. Used to notice the taskbar climbing over the overlay.
    /// </summary>
    internal static bool IsAbove(IntPtr candidate, IntPtr reference)
    {
        if (candidate == IntPtr.Zero || reference == IntPtr.Zero) return false;
        if (candidate == reference) return false;

        // GetWindow with GW_HWNDNEXT walks toward the bottom of the Z-order, so
        // reaching the candidate first means it is higher up the stack.
        var h = GetTopWindow(IntPtr.Zero);
        var guard = 0;

        while (h != IntPtr.Zero && guard++ < 5000)
        {
            if (h == candidate) return true;
            if (h == reference) return false;
            h = GetWindow(h, GW_HWNDNEXT);
        }

        return false;
    }
}
