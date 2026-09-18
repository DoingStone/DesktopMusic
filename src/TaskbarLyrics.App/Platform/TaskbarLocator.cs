using System.Windows.Interop;
using System.Windows.Media;
using TaskbarLyrics.App.Interop;

namespace TaskbarLyrics.App.Platform;

/// <summary>Which screen edge the taskbar sits on.</summary>
public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>
/// Taskbar geometry in device pixels, plus the derived DIP rectangle.
/// <para>
/// All rectangles here are in <b>physical device pixels</b> as returned by Win32.
/// WPF positions windows in <b>device-independent pixels</b>, so every value must
/// be divided by <see cref="Scale"/> before being assigned to
/// <c>Window.Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c> — otherwise the
/// overlay drifts and stretches on any display not running at 96 DPI.
/// </para>
/// </summary>
public sealed record TaskbarInfo(
    TaskbarEdge Edge,
    NativeMethods.RECT Bounds,
    NativeMethods.RECT MonitorBounds,
    double Scale)
{
    /// <summary>Taskbar thickness in DIP (height for horizontal bars).</summary>
    public double ThicknessDip => Edge is TaskbarEdge.Bottom or TaskbarEdge.Top
        ? Bounds.Height / Scale
        : Bounds.Width / Scale;

    public double LeftDip => Bounds.Left / Scale;
    public double TopDip => Bounds.Top / Scale;
    public double WidthDip => Bounds.Width / Scale;
    public double HeightDip => Bounds.Height / Scale;

    public double MonitorRightDip => MonitorBounds.Right / Scale;
}

/// <summary>
/// Locates the Windows taskbar so the lyric overlay can sit exactly on top of
/// it, in its empty region.
/// <para>
/// The overlay is anchored to the left edge of the notification area
/// (<c>TrayNotifyWnd</c>) rather than to the screen edge. That keeps lyrics
/// clear of the clock and system icons on every taskbar layout, and reacts
/// automatically when the tray resizes.
/// </para>
/// </summary>
public static class TaskbarLocator
{
    private const string ShellTrayWnd = "Shell_TrayWnd";
    private const string TrayNotifyWnd = "TrayNotifyWnd";

    /// <summary>Resolve the primary taskbar, or null when it cannot be found.</summary>
    /// <param name="visual">
    /// Optional WPF visual used to read the DPI scale. Passing the overlay window
    /// is preferred, because WPF's own <c>DpiScale</c> is the factor it applies to
    /// the window's Left/Top/Width/Height.
    /// </param>
    public static TaskbarInfo? Locate(Visual? visual = null)
    {
        var taskbar = NativeMethods.FindWindow(ShellTrayWnd, null);
        if (taskbar == IntPtr.Zero) return null;

        var bounds = NativeMethods.GetWindowBounds(taskbar);
        if (bounds is null || bounds.Value.Width <= 0 || bounds.Value.Height <= 0) return null;

        var rect = bounds.Value;
        var monitor = NativeMethods.MonitorFromWindow(taskbar, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorRect = GetMonitorRect(monitor) ?? rect;

        return new TaskbarInfo(
            DetectEdge(rect, monitorRect),
            rect,
            monitorRect,
            GetScale(visual, monitor));
    }

    /// <summary>
    /// Left edge of the notification area in device pixels. The overlay's right
    /// edge is placed here so it never overlaps the clock.
    /// </summary>
    public static double GetTrayLeftDevicePixels()
    {
        var taskbar = NativeMethods.FindWindow(ShellTrayWnd, null);
        if (taskbar == IntPtr.Zero) return double.NaN;

        var tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, TrayNotifyWnd, null);
        if (tray == IntPtr.Zero) return double.NaN;

        var trayRect = NativeMethods.GetWindowBounds(tray);
        return trayRect?.Left ?? double.NaN;
    }

    /// <summary>
    /// DPI scale converting device pixels to WPF DIP.
    /// <para>
    /// <c>VisualTreeHelper.GetDpi</c> is authoritative when a window is available,
    /// because that is exactly the factor WPF applies to <c>Window.Left</c>. The
    /// Win32 monitor DPI is the fallback.
    /// </para>
    /// </summary>
    private static double GetScale(Visual? visual, IntPtr monitor)
    {
        if (visual is not null)
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(visual);
                if (dpi.DpiScaleX > 0) return dpi.DpiScaleX;
            }
            catch
            {
                // Fall through to the Win32 query.
            }
        }

        // MDT_EFFECTIVE_DPI = 0
        if (monitor != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return dpiX / 96.0;
        }

        return 1.0;
    }

    private static TaskbarEdge DetectEdge(NativeMethods.RECT rect, NativeMethods.RECT monitorRect)
    {
        bool fullWidth = rect.Width >= monitorRect.Width - 2;
        bool fullHeight = rect.Height >= monitorRect.Height - 2;

        if (fullWidth)
        {
            return rect.Top <= monitorRect.Top + 2 ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        if (fullHeight)
        {
            return rect.Left <= monitorRect.Left + 2 ? TaskbarEdge.Left : TaskbarEdge.Right;
        }

        // Fall back to the common case.
        return TaskbarEdge.Bottom;
    }

    private static NativeMethods.RECT? GetMonitorRect(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return null;

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        return NativeMethods.GetMonitorInfo(monitor, ref info) ? info.rcMonitor : null;
    }
}
