using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TaskbarLyrics.App.Interop;

/// <summary>
/// Applies Windows 11 window chrome: rounded corners, a title bar tinted to the page,
/// and a Mica backdrop when the system supports it.
/// <para>
/// Mica needs <b>two</b> things, and the earlier attempt only did the second: the
/// client area must first be merged into the window frame with
/// <c>DwmExtendFrameIntoClientArea</c>, otherwise the backdrop has nowhere to render
/// and a transparent WPF client area simply paints black. That black client area is
/// why Mica was removed once already.
/// </para>
/// <para>
/// The surface brushes are swapped rather than hard-coded, so when Mica is
/// unavailable the window falls back to exactly the solid design instead of rendering
/// incorrectly.
/// </para>
/// </summary>
internal static class FluentChrome
{
    /// <summary>Windows 11 Settings page background.</summary>
    private static readonly Color PageColor = Color.FromRgb(0xF3, 0xF3, 0xF3);

    /// <summary>Navigation pane, a shade darker than the page.</summary>
    private static readonly Color NavColor = Color.FromRgb(0xED, 0xED, 0xED);

    // DWMWINDOWATTRIBUTE
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // DWM_SYSTEMBACKDROP_TYPE
    private const int DWMSBT_MAINWINDOW = 2; // Mica

    // DWM_WINDOW_CORNER_PREFERENCE
    private const int DWMWCP_ROUND = 2;

    /// <summary>First Windows 11 build that has the system backdrop attribute.</summary>
    private const int Windows11Build = 22000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    /// <summary>Apply the window chrome and, where possible, a Mica backdrop.</summary>
    /// <returns>True when Mica was applied and the translucent surfaces are in use.</returns>
    public static bool Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;

        // Light mode so the system-drawn caption text and buttons are dark.
        TrySetInt(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, 0);
        TrySetInt(handle, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

        bool mica = TryEnableMica(handle);

        if (mica)
        {
            // The whole client area is now frame, so nothing may paint an opaque
            // background over it. The caption is deliberately left untinted so it
            // picks up the Mica tint and matches the body.
            window.Background = Brushes.Transparent;
            ApplySurfaces(window,
                windowBg: Color.FromArgb(0x00, 0, 0, 0), // transparent: Mica shows through
                navBg: Color.FromArgb(0x00, 0, 0, 0),
                contentBg: Color.FromArgb(0x66, 0xF3, 0xF3, 0xF3),
                card: Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF),
                cardBorder: Color.FromArgb(0x14, 0, 0, 0),
                navItemHover: Color.FromArgb(0x14, 0, 0, 0));

            Diag.Log("[fluent] Mica backdrop enabled");
        }
        else
        {
            // Solid fallback: identical layout and palette, just opaque.
            TrySetColor(handle, DWMWA_CAPTION_COLOR, PageColor);
            window.Background = new SolidColorBrush(PageColor);
            ApplySurfaces(window,
                windowBg: PageColor,
                navBg: NavColor,
                contentBg: PageColor,
                card: Colors.White,
                cardBorder: Color.FromRgb(0xE5, 0xE5, 0xE5),
                navItemHover: Color.FromArgb(0x14, 0, 0, 0));

            Diag.Log("[fluent] Mica unavailable; using the solid palette");
        }

        return mica;
    }

    /// <summary>
    /// Merge the client area into the window frame and request the Mica backdrop.
    /// Both steps are required: without the frame extension the backdrop cannot render.
    /// </summary>
    private static bool TryEnableMica(IntPtr handle)
    {
        // Escape hatch. If a machine renders the extended frame badly there is no way
        // to detect that from inside the process, so this gives a one-variable way
        // back to the solid palette.
        if (Environment.GetEnvironmentVariable("TBL_NO_MICA") == "1") return false;

        if (Environment.OSVersion.Version.Build < Windows11Build) return false;

        try
        {
            // -1 on every side = "sheet of glass": extend the frame over the whole
            // client area.
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(handle, ref margins) != 0) return false;

            if (!TrySetInt(handle, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW))
            {
                // The frame is extended but there is no backdrop to fill it, which
                // would paint black. Put the frame back before giving up.
                var none = new MARGINS();
                DwmExtendFrameIntoClientArea(handle, ref none);
                return false;
            }

            return true;
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

    /// <summary>
    /// Overwrite the surface brushes the XAML pulls in with DynamicResource, so the
    /// switch between Mica and solid actually takes effect at runtime.
    /// </summary>
    private static void ApplySurfaces(
        Window window, Color windowBg, Color navBg, Color contentBg,
        Color card, Color cardBorder, Color navItemHover)
    {
        var resources = window.Resources;

        void Set(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }

        Set("WindowBg", windowBg);
        Set("NavBg", navBg);
        Set("ContentBg", contentBg);
        Set("Card", card);
        Set("CardBorder", cardBorder);
        Set("NavItemHover", navItemHover);
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
}
