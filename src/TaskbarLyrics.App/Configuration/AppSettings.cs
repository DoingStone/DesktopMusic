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

    /// <summary>
    /// Lyric font size in DIP.
    /// <para>
    /// 11 matches the reference implementation (Soda Music's taskbar widget uses
    /// <c>font-size:11px</c>). A taskbar strip is a glanceable surface: at this size
    /// the whole line sits on one calm baseline instead of dominating the bar, which
    /// is most of why the reference reads as part of the shell rather than as a
    /// sticker on top of it.
    /// </para>
    /// </summary>
    public double OriginalFontSize { get; set; } = 11.0;

    public double TranslationFontSize { get; set; } = 10.0;
    public bool ShowTranslation { get; set; } = true;

    /// <summary>
    /// Derive the lyric palette from the taskbar behind the strip instead of using
    /// the colours below.
    /// <para>
    /// The reference implementation never picks a lyric colour by hand: it asks the
    /// shell whether the surface is light or dark (<c>light-dark()</c>) and then uses
    /// the same hue at two alphas — 50% for unsung, 90% for sung. Over a light
    /// taskbar that means black text, over a dark one white text, so the strip keeps
    /// the bar's own look instead of laying a fixed colour on top of it. The sampled
    /// luminance is the same reading that already picks the transport glyph colour,
    /// so this adds no new probing.
    /// </para>
    /// </summary>
    public bool AutoAdaptColors { get; set; } = true;

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

    /// <summary>
    /// Draw the optional readability plate behind the text.
    /// <para>
    /// Off by default: the reference implementation's docked state is fully
    /// transparent and only raises a plate while the widget is dragged out of the
    /// taskbar. With <see cref="AutoAdaptColors"/> handling legibility, a permanent
    /// plate is what makes a strip look pasted onto the bar rather than part of it.
    /// Turn it on for wallpapers whose taskbar sample is unreliable (a busy gradient
    /// or a maximised window showing through an acrylic bar).
    /// </para>
    /// </summary>
    public bool ShowBackground { get; set; }
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
    /// Keep the transport controls invisible until the cursor is over the strip.
    /// <para>
    /// The reference implementation's docked widget is lyrics plus cover art and
    /// nothing else; its buttons overlay the strip only while the pointer is inside
    /// it. Only meaningful while the strip is interactive — a click-through overlay
    /// receives no mouse messages, so its controls must stay visible to be usable at
    /// all. Ignored when the hover cluster would be empty anyway — that is, when
    /// <see cref="ShowTransportControls"/>, <see cref="ShowCoverArt"/>,
    /// <see cref="ShowSongTitle"/> and <see cref="ShowSongArtist"/> are all off.
    /// </para>
    /// </summary>
    public bool HoverRevealControls { get; set; } = true;

    /// <summary>
    /// Fade the incoming lyric line in over ~110 ms instead of swapping the text on
    /// a single frame. Lines change every few seconds, so the cost is negligible and
    /// the change stops reading as a flicker.
    /// </summary>
    public bool LineTransition { get; set; } = true;

    /// <summary>
    /// Show the song progress bar and time readout beneath the transport buttons.
    /// </summary>
    public bool ShowSongProgress { get; set; } = true;

    /// <summary>Progress bar fill colour.</summary>
    public string ProgressBarColor { get; set; } = "#FF3ABEFF";

    /// <summary>
    /// Show the song title in the cluster that appears on hover, next to the cover.
    /// <para>
    /// Track information is deliberately hover-only: while the pointer is away the
    /// strip holds nothing but the words, which is what lets the lyric line sit in
    /// the middle of the whole band instead of only in the space the cluster leaves.
    /// </para>
    /// </summary>
    public bool ShowSongTitle { get; set; } = true;

    /// <summary>Show the artist beneath the title in the hover cluster.</summary>
    public bool ShowSongArtist { get; set; } = true;

    /// <summary>
    /// Show the album art in the hover cluster. The art is read from the player's own
    /// media session (SMTC thumbnail), so it is whatever the player published — no
    /// extra network request and no per-player special case.
    /// </summary>
    public bool ShowCoverArt { get; set; } = true;

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

    /// <summary>Navigation pane width in DIP, as left by the sash.</summary>
    public double NavWidth { get; set; } = 232;

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
    /// <summary>
    /// Look up per-word (TTML) lyrics from the AMLL TTML DB before falling back to
    /// line-level sources. Word timing is what makes the highlight track the singing
    /// exactly, so this is on by default; it costs one lookup per track.
    /// </summary>
    public bool EnableWordLyrics { get; set; } = true;

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

    /// <summary>
    /// The settings file this run works against. <c>TBL_SETTINGS</c> points it somewhere else,
    /// which is how the verification scripts exercise a mode without reading or writing the
    /// settings of whoever is signed in — the same escape hatch <c>TBL_DIAG</c> and
    /// <c>TBL_COMPOSITE</c> already provide for the log and the compositing mode.
    /// </summary>
    public static string ConfigPath =>
        Environment.GetEnvironmentVariable("TBL_SETTINGS") is { Length: > 0 } custom
            ? custom
            : Path.Combine(ConfigDirectory, "settings.json");

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
