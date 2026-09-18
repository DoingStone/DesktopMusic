using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace TaskbarLyrics.App;

/// <summary>
/// Loads the embedded master icon artwork.
/// <para>
/// The executable's icon comes from the .ico (see <c>ApplicationIcon</c>), but WPF
/// windows and the notification area need the image itself. Both read this one
/// embedded resource, so the artwork exists in exactly one place.
/// </para>
/// </summary>
internal static class AppIcons
{
    private const string ResourceName = "TaskbarLyrics.AppIcon.png";

    private static BitmapFrame? _cached;

    /// <summary>The icon as a WPF image source, or null when unavailable.</summary>
    public static BitmapFrame? Image => _cached ??= Load();

    /// <summary>Apply the icon to a window, ignoring failures.</summary>
    public static void ApplyTo(Window window)
    {
        var image = Image;
        if (image is null) return;

        try
        {
            window.Icon = image;
        }
        catch
        {
            // A missing icon must never stop a window from opening.
        }
    }

    private static BitmapFrame? Load()
    {
        try
        {
            using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                Diag.Log("[icon] embedded artwork not found");
                return null;
            }

            // OnLoad keeps the frame usable after the stream is disposed.
            return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch (Exception ex)
        {
            Diag.Log($"[icon] load failed: {ex.Message}");
            return null;
        }
    }
}
