using System.Runtime.InteropServices;

namespace TaskbarLyrics.App.Interop;

/// <summary>
/// Reports the process DPI-awareness mode.
/// <para>
/// This matters because the overlay positions itself using physical pixels from
/// the taskbar window. If the process is DPI-unaware, Windows silently
/// virtualises those coordinates, and the reported monitor size no longer
/// matches what the user actually sees — which puts the overlay in the wrong
/// place on scaled displays.
/// </para>
/// </summary>
internal static class DpiInfo
{
    public static string Describe()
    {
        try
        {
            var context = GetThreadDpiAwarenessContext();
            var awareness = GetAwarenessFromDpiAwarenessContext(context);
            return awareness switch
            {
                0 => "INVALID",
                1 => "UNAWARE",
                2 => "SYSTEM_AWARE",
                3 => "PER_MONITOR_AWARE",
                4 => "PER_MONITOR_AWARE_V2",
                5 => "UNAWARE_GDISCALED",
                _ => $"UNKNOWN({awareness})",
            };
        }
        catch
        {
            return "unknown";
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
}
