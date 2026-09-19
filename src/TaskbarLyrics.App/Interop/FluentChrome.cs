using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TaskbarLyrics.App.Interop;

/// <summary>
/// Applies Windows 11 window chrome: rounded corners, a caption in the right light or
/// dark mode, and a Mica backdrop when the system supports it and the user wants it.
/// <para>
/// Mica needs <b>two</b> things: the client area must be merged into the window frame
/// with <c>DwmExtendFrameIntoClientArea</c>, and the backdrop type must be set.
/// Requesting only the latter leaves a transparent client area painting black.
/// </para>
/// <para>
/// Note also that <c>WindowChrome.GlassFrameThickness</c> in the window's XAML applies
/// the same frame. Setting it to 0 cancels this call and reintroduces the black client
/// area, which is why it must be -1 for the backdrop to render.
/// </para>
/// </summary>
internal static class FluentChrome
{
    /// <summary>Theme selection values stored in settings.</summary>
    public const string ThemeSystem = "system";
    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";

    // DWMWINDOWATTRIBUTE
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMSBT_MAINWINDOW = 2; // Mica
    private const int DWMWCP_ROUND = 2;

    /// <summary>First Windows 11 build with the system backdrop attribute.</summary>
    private const int Windows11Build = 22000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    /// <summary>Palette for one theme.</summary>
    private readonly record struct Palette(
        Color WindowBg, Color NavBg, Color ContentBg, Color NavItemFg, Color NavItemHover,
        Color Card, Color CardBorder, Color ControlFill, Color ControlBorder, Color ControlHover,
        Color Fg, Color FgMuted, Color Line, Color Accent);

    // Windows 11 light. With Mica the window body is transparent so the material shows;
    // the nav pane keeps a slight darkening and the content a light layer, which is what
    // separates the two regions.
    private static readonly Palette Light = new(
        WindowBg: Color.FromRgb(0xF3, 0xF3, 0xF3),
        NavBg: Color.FromArgb(0x1A, 0x00, 0x00, 0x00),
        ContentBg: Color.FromArgb(0x66, 0xF3, 0xF3, 0xF3),
        NavItemFg: Color.FromRgb(0x1A, 0x1A, 0x1A),
        NavItemHover: Color.FromArgb(0x14, 0x00, 0x00, 0x00),
        Card: Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF),
        CardBorder: Color.FromArgb(0x14, 0x00, 0x00, 0x00),
        ControlFill: Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF),
        ControlBorder: Color.FromArgb(0xFF, 0xD6, 0xD6, 0xD6),
        ControlHover: Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5),
        Fg: Color.FromRgb(0x1A, 0x1A, 0x1A),
        FgMuted: Color.FromRgb(0x5D, 0x5D, 0x5D),
        Line: Color.FromArgb(0x1F, 0x00, 0x00, 0x00),
        Accent: Color.FromRgb(0x00, 0x67, 0xC0));

    // Windows 11 dark.
    private static readonly Palette Dark = new(
        WindowBg: Color.FromRgb(0x20, 0x20, 0x20),
        NavBg: Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF),
        ContentBg: Color.FromArgb(0x4D, 0x20, 0x20, 0x20),
        NavItemFg: Color.FromRgb(0xFF, 0xFF, 0xFF),
        NavItemHover: Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
        Card: Color.FromArgb(0xB3, 0x2C, 0x2C, 0x2C),
        CardBorder: Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
        ControlFill: Color.FromArgb(0xCC, 0x2D, 0x2D, 0x2D),
        ControlBorder: Color.FromArgb(0xFF, 0x4A, 0x4A, 0x4A),
        ControlHover: Color.FromArgb(0xFF, 0x38, 0x38, 0x38),
        Fg: Color.FromRgb(0xFF, 0xFF, 0xFF),
        FgMuted: Color.FromRgb(0xC5, 0xC5, 0xC5),
        Line: Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
        Accent: Color.FromRgb(0x60, 0xCD, 0xFF));

    /// <summary>
    /// Apply chrome for the configured theme and material. Safe to call again after the
    /// user changes either, which is what makes the settings live.
    /// </summary>
    /// <param name="theme">system | light | dark</param>
    /// <param name="useMica">Whether a backdrop may be requested.</param>
    public static void Apply(Window window, string theme, bool useMica)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        bool dark = theme switch
        {
            ThemeDark => true,
            ThemeLight => false,
            _ => IsSystemDark(),
        };

        try
        {
            // Dark caption buttons and light caption text when the theme is dark.
            TrySetInt(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
            TrySetInt(handle, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        }
        catch
        {
            // Chrome is cosmetic: never let it stop the window from opening.
        }

        // The environment variable stays as a hard override for diagnosing a bad
        // composite on a machine where the material misbehaves.
        bool wantMica = useMica && Environment.GetEnvironmentVariable("TBL_NO_MICA") != "1";
        bool mica = wantMica && TryEnableMica(handle);

        if (mica)
        {
            window.Background = Brushes.Transparent;
        }
        else
        {
            var solid = dark ? Dark.WindowBg : Light.WindowBg;
            TrySetColor(handle, DWMWA_CAPTION_COLOR, solid);
            window.Background = new SolidColorBrush(solid);
        }

        ApplyPalette(window, dark ? Dark : Light, mica);

        Diag.Log($"[fluent] theme={(dark ? "dark" : "light")} requested={theme} mica={mica}");
    }

    /// <summary>Whether the system is using a dark app theme.</summary>
    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Merge the client area into the window frame and request the Mica backdrop. Both
    /// steps are required: without the frame extension there is nowhere to render.
    /// </summary>
    private static bool TryEnableMica(IntPtr handle)
    {
        if (Environment.OSVersion.Version.Build < Windows11Build) return false;

        try
        {
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(handle, ref margins) != 0) return false;

            if (!TrySetInt(handle, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW))
            {
                // Frame extended with no backdrop to fill it would paint black, so put
                // the frame back before giving up.
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
    /// Replace the surface brushes the XAML pulls in with DynamicResource. Without Mica
    /// the translucent values are flattened onto their solid equivalents so the window
    /// is opaque and correct rather than showing the desktop through it.
    /// </summary>
    private static void ApplyPalette(Window window, Palette p, bool mica)
    {
        var resources = window.Resources;

        void Set(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }

        Set("Fg", p.Fg);
        Set("FgMuted", p.FgMuted);
        Set("FgHeading", p.Fg);
        Set("Line", p.Line);
        Set("Accent", p.Accent);

        Set("Card", mica ? p.Card : Color.FromRgb(p.Card.R, p.Card.G, p.Card.B));
        Set("CardBorder", mica ? p.CardBorder : Color.FromRgb(p.Line.R, p.Line.G, p.Line.B));

        Set("ControlFill", mica ? p.ControlFill : Color.FromRgb(p.ControlFill.R, p.ControlFill.G, p.ControlFill.B));
        Set("ControlBorder", p.ControlBorder);
        Set("ControlHover", p.ControlHover);

        Set("WindowBg", mica ? Color.FromArgb(0x00, 0, 0, 0) : p.WindowBg);
        Set("ContentBg", mica ? p.ContentBg : p.WindowBg);
        Set("NavBg", mica ? p.NavBg : Blend(p.WindowBg, p.NavBg));
        Set("NavItemFg", p.NavItemFg);
        Set("NavItemHover", p.NavItemHover);
    }

    /// <summary>Flatten a translucent tint onto an opaque base.</summary>
    private static Color Blend(Color baseColor, Color tint)
    {
        double a = tint.A / 255.0;

        return Color.FromRgb(
            (byte)Math.Round((tint.R * a) + (baseColor.R * (1 - a))),
            (byte)Math.Round((tint.G * a) + (baseColor.G * (1 - a))),
            (byte)Math.Round((tint.B * a) + (baseColor.B * (1 - a))));
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
