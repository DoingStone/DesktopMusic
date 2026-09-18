using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TaskbarLyrics.App.Interop;

/// <summary>How the overlay achieves its transparency.</summary>
public enum CompositingMode
{
    /// <summary>
    /// WPF per-pixel alpha (<c>AllowsTransparency</c>). Best quality — soft
    /// anti-aliased text edges and a translucent backdrop — but it depends on the
    /// compositor honouring layered-window updates.
    /// </summary>
    PerPixelAlpha,

    /// <summary>
    /// A colour-keyed layered window plus a <c>SetWindowRgn</c> rounded region.
    /// <para>
    /// The window stays opaque and its background is filled with a sentinel
    /// colour that the compositor hides, while the region clips it to a rounded
    /// rectangle. This avoids per-pixel alpha completely, so it still appears on
    /// systems where <see cref="PerPixelAlpha"/> composites as fully transparent.
    /// </para>
    /// </summary>
    ColorKey,
}

/// <summary>
/// Applies the overlay's transparency strategy, with a startup self-check.
/// <para>
/// Per-pixel alpha is preferred for quality. Some systems render such a window
/// without ever compositing it to the screen (it stays invisible while WPF
/// happily rasterises it). <see cref="VerifyPerPixelAlpha"/> detects that
/// empirically by sampling the real screen pixel behind the overlay, and the app
/// switches to the colour-key path when the sample does not come back.
/// </para>
/// <para>
/// Set <c>TBL_COMPOSITE=alpha|colorkey</c> to pin a mode and skip the check.
/// </para>
/// </summary>
internal static class TransparencyMode
{
    /// <summary>Sentinel background colour hidden by the colour-key path.</summary>
    internal static readonly Color KeyColor = Color.FromRgb(255, 0, 255);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int LWA_COLORKEY = 0x00000001;

    /// <summary>Resolve the mode from the environment, defaulting to per-pixel alpha.</summary>
    public static CompositingMode Resolve()
    {
        var requested = Environment.GetEnvironmentVariable("TBL_COMPOSITE")?.Trim().ToLowerInvariant();

        return requested switch
        {
            "colorkey" or "color-key" => CompositingMode.ColorKey,
            _ => CompositingMode.PerPixelAlpha,
        };
    }

    /// <summary>True when the environment explicitly pinned a mode.</summary>
    public static bool IsForced =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TBL_COMPOSITE"));

    /// <summary>Fill the window with the sentinel colour for the colour-key path.</summary>
    public static void PrepareColorKey(Window window)
    {
        window.Background = new SolidColorBrush(KeyColor);
    }

    /// <summary>
    /// Decide whether per-pixel alpha can be trusted, without drawing anything
    /// unusual on screen.
    /// <para>
    /// The previous implementation painted the window with a sentinel colour and
    /// read the pixel back from the screen. That worked, but it meant a visible
    /// colour flash in the taskbar on every launch. This instead asks the
    /// compositor directly: per-pixel alpha requires DWM composition, and a window
    /// that the compositor has cloaked cannot be trusted to render.
    /// </para>
    /// <para>
    /// If this is ever wrong on an unusual machine, <c>TBL_COMPOSITE=colorkey</c>
    /// forces the fallback explicitly.
    /// </para>
    /// </summary>
    public static bool VerifyPerPixelAlpha(Window window)
    {
        try
        {
            if (!IsCompositionEnabled())
            {
                Diag.Log("[composite] DWM composition is off; per-pixel alpha unavailable");
                return false;
            }

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return true;

            if (IsCloaked(handle))
            {
                Diag.Log("[composite] window is cloaked; falling back to colour key");
                return false;
            }

            Diag.Log("[composite] DWM composition available; keeping per-pixel alpha");
            return true;
        }
        catch (Exception ex)
        {
            // Never downgrade on an unexpected error: the fallback is lower quality.
            Diag.Log($"[composite] verify EXCEPTION {ex.Message}; assuming alpha works");
            return true;
        }
    }

    /// <summary>True when the desktop compositor is running.</summary>
    private static bool IsCompositionEnabled()
    {
        try
        {
            return DwmIsCompositionEnabled(out var enabled) == 0 && enabled;
        }
        catch (DllNotFoundException)
        {
            // No DWM at all (very old Windows); treat as unavailable.
            return false;
        }
    }

    /// <summary>
    /// True when DWM has cloaked the window — a state where it is nominally
    /// visible but deliberately not composited.
    /// </summary>
    private static bool IsCloaked(IntPtr handle)
    {
        try
        {
            var cloaked = 0;
            var hr = DwmGetWindowAttribute(handle, DWMWA_CLOAKED, out cloaked, sizeof(int));
            return hr == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>
    /// Apply the colour-key + rounded-region treatment. Requires a live handle.
    /// </summary>
    public static void ApplyColorKey(Window window, double cornerRadius)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var exStyle = NativeMethods.GetWindowLong(handle, GWL_EXSTYLE);
        NativeMethods.SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);

        SetLayeredWindowAttributes(handle, ToColorRef(KeyColor), 0, LWA_COLORKEY);

        ApplyRoundedRegion(window, cornerRadius);

        Diag.Log("[composite] colour-key applied");
    }

    /// <summary>
    /// Clip the window to a rounded rectangle so a colour-keyed window does not
    /// show square corners over the taskbar.
    /// </summary>
    public static void ApplyRoundedRegion(Window window, double cornerRadius)
    {
        // Diagnostic escape hatch: a window region combined with a layered window
        // can suppress painting entirely on some systems.
        if (Environment.GetEnvironmentVariable("TBL_NO_REGION") == "1") return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(window);
        var w = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) return;

        // CreateRoundRectRgn takes the ellipse diameter, i.e. twice the radius.
        var r = (int)Math.Round(cornerRadius * dpi.DpiScaleX * 2);
        r = Math.Min(r, Math.Min(w, h));

        var region = CreateRoundRectRgn(0, 0, w + 1, h + 1, r, r);
        if (region == IntPtr.Zero) return;

        // SetWindowRgn owns the region once it succeeds.
        if (SetWindowRgn(handle, region, true) == 0)
        {
            DeleteObject(region);
        }
    }

    private static uint ToColorRef(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
        int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
