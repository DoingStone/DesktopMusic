using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarLyrics.App.Configuration;

/// <summary>
/// User settings, persisted as JSON under <c>%APPDATA%\TaskbarLyrics</c>.
/// </summary>
public sealed class AppSettings
{
    // ---- appearance ----------------------------------------------------
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double OriginalFontSize { get; set; } = 12.5;
    public double TranslationFontSize { get; set; } = 11.0;
    public bool ShowTranslation { get; set; } = true;

    /// <summary>Colour of the already-sung portion of the current line.</summary>
    public string HighlightColor { get; set; } = "#FF3ABEFF";

    /// <summary>Colour of the not-yet-sung portion of the current line.</summary>
    public string BaseColor { get; set; } = "#FFE8E8E8";

    /// <summary>Colour of neighbouring (non-current) lines.</summary>
    public string ContextColor { get; set; } = "#8CFFFFFF";

    /// <summary>
    /// Optional panel behind the text, for readability over bright wallpapers.
    /// <para>
    /// Kept both dark and fairly transparent: a light-on-dark taskbar theme would
    /// otherwise wash out a mid-grey panel, and a fully opaque one would hide too
    /// much of the taskbar.
    /// </para>
    /// </summary>
    public string BackgroundColor { get; set; } = "#B3121212";

    public bool ShowContextLines { get; set; } = true;
    public bool ShowBackground { get; set; } = true;
    public double BackgroundCornerRadius { get; set; } = 5;

    /// <summary>Enables the karaoke sweep highlight on the current line.</summary>
    public bool EnableWordHighlight { get; set; } = true;

    /// <summary>
    /// Overall opacity of the lyric overlay, 0.15–1.0. Applied to the whole window
    /// so text and panel fade together.
    /// </summary>
    public double OverlayOpacity { get; set; } = 1.0;

    /// <summary>
    /// <c>Normal</c>, <c>SemiBold</c> or <c>Bold</c>. Affects legibility against
    /// busy wallpapers.
    /// </summary>
    public string FontWeight { get; set; } = "SemiBold";

    /// <summary>
    /// Extra spacing between characters, in DIP. Zero keeps the font default.
    /// Useful for airy, sparse lyrics.
    /// </summary>
    public double LetterSpacing { get; set; }

    /// <summary>
    /// Overlay height in DIP. Zero means "fit the taskbar automatically", which is
    /// right unless the user wants a slimmer or taller strip.
    /// </summary>
    public double Height { get; set; }

    /// <summary>
    /// Vertical nudge as a fraction of the taskbar height (−1 to +1). Keeps the
    /// overlay inside the band while allowing fine optical alignment.
    /// </summary>
    public double VerticalAlign { get; set; }

    /// <summary>
    /// Float the strip just above the taskbar instead of inside it.
    /// <para>
    /// The taskbar is topmost, and Explorer re-raises it above other topmost windows
    /// when it is activated. A strip drawn inside the bar is therefore covered for
    /// ~200 ms on every taskbar click, which reads as a flicker and cannot be fully
    /// prevented from a top-level window. Sitting above the bar removes the overlap
    /// entirely, at the cost of not being inside the taskbar itself.
    /// </para>
    /// </summary>
    public bool PlaceAboveTaskbar { get; set; }

    // ---- playback hotkeys ----------------------------------------------
    // Empty text disables that shortcut. Format: "Ctrl+Alt+Space", "Ctrl+Alt+Right".
    // Registered system-wide so playback can be driven while the overlay stays
    // click-through.

    public string HotkeyPlayPause { get; set; } = "Ctrl+Alt+Space";
    public string HotkeyNextTrack { get; set; } = "Ctrl+Alt+Right";
    public string HotkeyPreviousTrack { get; set; } = "Ctrl+Alt+Left";
    public string HotkeyToggleOverlay { get; set; } = "Ctrl+Alt+L";

    // ---- placement -----------------------------------------------------
    /// <summary>
    /// Horizontal offset in DIP from the default anchor. Positive moves right.
    /// This is what the user changes by dragging the overlay.
    /// </summary>
    public double OffsetX { get; set; }

    /// <summary>Vertical offset in DIP from the taskbar centre.</summary>
    public double OffsetY { get; set; }

    /// <summary>Overlay width in DIP.</summary>
    public double Width { get; set; } = 460;

    /// <summary>
    /// When false, the overlay is click-through and the taskbar underneath stays
    /// fully usable. When true the overlay accepts mouse input so it can be dragged.
    /// </summary>
    public bool Locked { get; set; } = true;

    /// <summary>
    /// Whether the lyric text and transport buttons respond to the mouse (so the
    /// strip can be dragged and the buttons clicked).
    /// <para>
    /// This is the inverse of the persisted <see cref="Locked"/> flag, exposed the
    /// right way round because "interactive" is what the UI talks about now.
    /// Kept as an alias so existing settings files keep working and the JSON stays
    /// free of two competing flags.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public bool Interactive
    {
        get => !Locked;
        set => Locked = !value;
    }

    /// <summary>
    /// Show transport buttons (previous / play-pause / next) inside the lyric
    /// strip. Buttons are hit-testable, so they remain clickable even while the
    /// rest of the strip stays click-through.
    /// </summary>
    public bool ShowTransportControls { get; set; } = true;

    /// <summary>
    /// Show the song progress bar and time readout beneath the transport buttons.
    /// </summary>
    public bool ShowSongProgress { get; set; } = true;

    /// <summary>Progress bar fill colour.</summary>
    public string ProgressBarColor { get; set; } = "#FF3ABEFF";

    /// <summary>
    /// Colour theme for the settings window: <c>system</c>, <c>light</c> or <c>dark</c>.
    /// </summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// Use the Mica backdrop where available. Kept switchable because a material that
    /// fails to composite cannot be detected from inside the process, so the solid
    /// palette must stay reachable without a rebuild.
    /// </summary>
    public bool UseMica { get; set; } = true;

    /// <summary>Whether the settings navigation pane is collapsed.</summary>
    public bool NavCollapsed { get; set; }

    /// <summary>Remembered position, keyed per monitor+taskbar signature.</summary>
    public Dictionary<string, PositionMemory> Positions { get; set; } = new();

    // ---- behaviour -----------------------------------------------------
    public bool ShowWhenPaused { get; set; } = true;
    public bool HideWhenNoLyrics { get; set; } = true;
    public bool Visible { get; set; } = true;

    /// <summary>Per-source playback offset in milliseconds; positive delays lyrics.</summary>
    public int GlobalOffsetMs { get; set; }

    public bool StartWithWindows { get; set; }

    // ---- providers -----------------------------------------------------
    public bool EnableQqMusic { get; set; } = true;
    public bool EnableNetEase { get; set; } = true;
    public bool EnableLrclib { get; set; } = true;

    // ---- persistence ---------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TaskbarLyrics");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppSettings();

            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // A corrupt settings file must never prevent the app from starting.
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(this, JsonOptions);

            // Write to a temp file then move, so a crash cannot truncate settings.
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch
        {
            // Persistence failures are non-fatal.
        }
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)
        ?? new AppSettings();
}

/// <summary>Saved overlay position for a particular monitor/taskbar arrangement.</summary>
public sealed class PositionMemory
{
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Width { get; set; } = 460;
}
