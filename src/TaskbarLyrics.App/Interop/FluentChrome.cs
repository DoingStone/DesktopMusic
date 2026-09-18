using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TaskbarLyrics.App.Interop;

/// <summary>
/// Applies Windows 11 window chrome: rounded corners and a title bar tinted to
/// match the page.
/// <para>
/// Deliberately does <b>not</b> request a Mica backdrop. Mica only renders when the
/// client area has first been merged into the window frame with
/// <c>DwmExtendFrameIntoClientArea</c>; without that step a transparent WPF client
/// area paints solid black, which is exactly the black-on-white appearance this
/// replaces. The Windows 11 Settings palette is applied as real colours instead, so
/// the window cannot end up with a black client area regardless of the system.
/// </para>
/// </summary>
internal static class FluentChrome
{
    /// <summary>Windows 11 Settings page background.</summary>
    private static readonly Color PageColor = Color.FromRgb(0xF3, 0xF3, 0xF3);

    // DWMWINDOWATTRIBUTE
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_CAPTION_COLOR = 35;

    // DWM_WINDOW_CORNER_PREFERENCE
    private const int DWMWCP_ROUND = 2;

    /// <summary>Apply rounded corners, light title bar, and the page background.</summary>
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        // Light mode so the system-drawn title text and caption buttons are dark.
        TrySetInt(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, 0);
        TrySetInt(handle, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

        // Tint the caption to the page colour so the title bar and body match.
        TrySetColor(handle, DWMWA_CAPTION_COLOR, PageColor);

        window.Background = new SolidColorBrush(PageColor);
    }

    private static bool TrySetInt(IntPtr handle, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>DWM colour attributes take a COLORREF (0x00BBGGRR).</summary>
    private static bool TrySetColor(IntPtr handle, int attribute, Color color)
    {
        int colorRef = color.R | (color.G << 8) | (color.B << 16);

        try
        {
            return DwmSetWindowAttribute(handle, attribute, ref colorRef, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
