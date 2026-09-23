using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarLyrics.App;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.App.Controls;

/// <summary>
/// Renders one lyric line with a karaoke-style progress highlight.
/// <para>
/// The element draws the line twice: once in the base colour and once in the
/// highlight colour, the latter clipped to a vertical edge whose x-position is
/// the accumulated width of everything sung so far. Because both passes use
/// <see cref="FormattedText"/> built from the same runs, glyph advance widths
/// match exactly and the sweep lands on real character boundaries.
/// </para>
/// <para>
/// Highlight granularity is per-syllable: the line is split into
/// <see cref="LyricLine.Syllables"/> when the source supplied word timing, and
/// otherwise each character is treated as an equal-width syllable. Either way
/// the sweep advances word-by-word rather than as one smooth smear across the
/// whole line.
/// </para>
/// </summary>
public sealed class KaraokeLine : FrameworkElement
{
    public KaraokeLine()
    {
        // Clip rendering to our own bounds: without this an over-wide line draws
        // straight over the transport buttons.
        ClipToBounds = true;

        Loaded += (_, _) => EnsureScrollTimer();
        Unloaded += (_, _) => StopScrollTimer();
    }

    private static readonly Typeface FallbackTypeface =
        new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Brush ShadowBrush = CreateShadowBrush();

    private static Brush CreateShadowBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(KaraokeLine),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender, OnTextChanged));

    public static readonly DependencyProperty TranslationProperty =
        DependencyProperty.Register(nameof(Translation), typeof(string), typeof(KaraokeLine),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Highlight progress within the line, 0..1.</summary>
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(KaraokeLine),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>True when this is the line currently being sung.</summary>
    public static readonly DependencyProperty IsCurrentProperty =
        DependencyProperty.Register(nameof(IsCurrent), typeof(bool), typeof(KaraokeLine),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    // ---- marquee scrolling ---------------------------------------------
    //
    // When the strip is narrower than the lyric, centring the text made it spill out
    // of both sides and overlap the transport buttons. Overflowing lines now scroll
    // instead, clipped to the control's own bounds.

    /// <summary>Scroll speed in DIP per second. Slow enough to read comfortably.</summary>
    private const double ScrollSpeed = 30;

    /// <summary>Blank space kept between the end of the text and the restart.</summary>
    private const double ScrollRepeatGap = 48;

    /// <summary>Pause before scrolling starts, so a new line is readable at once.</summary>
    private const double ScrollInitialHoldMs = 900;

    /// <summary>Frame interval; ~30 fps keeps the motion smooth but cheap.</summary>
    private const int ScrollFrameMs = 33;

    // ---- highlight-driven follow (P2) -----------------------------------
    //
    // Where the sweep edge is held once the singing has caught up with it: the words that
    // are still to come stay visible in the remaining tenth of the room. The offset chases
    // this target exponentially instead of at a fixed speed, so the line reads as being
    // carried along by the voice rather than as a marquee.

    private const double FollowFraction = 0.9;
    private const double FollowRate = 8.0;
    private const double FollowSnapPx = 0.5;

    /// <summary>Share of the font size that the sweep's leading edge fades across (P3).</summary>
    private const double HighlightFadeRatio = 0.45;

    private double _scrollOffset;
    private double _scrollHoldMs;
    private double _measuredTextWidth;
    private DispatcherTimer? _scrollTimer;

    /// <summary>Timestamp of the previous follow step, so it can use a real frame delta.</summary>
    private long _followTicks;

    /// <summary>Progress seen in the previous pass; the follow steers by how that moves.</summary>
    private double _lastProgress = double.NaN;

    /// <summary>True while the sweep is advancing, i.e. while the follow owns the offset.</summary>
    private bool _singing;

    /// <summary>
    /// Offset that was last written to the diag log, so the follow reports sparsely. Starts at
    /// negative infinity rather than NaN: a NaN sentinel makes every comparison false and the
    /// line would never be reported at all.
    /// </summary>
    private double _diagFollowOffset = double.NegativeInfinity;

    // ---- text layout cache ---------------------------------------------
    //
    // Every pass used to build its own FormattedText, and building one runs the whole text
    // layout: shaping, advance widths, line metrics. With up to four passes per frame plus a
    // per-character measurement that was the bulk of the render cost and of the garbage.
    // These objects only depend on the line, the typeface and the DPI, so they are built once
    // and afterwards recoloured through FormattedText.SetForegroundBrush, which does not
    // re-run layout.

    private FormattedText? _cachedMain;
    private FormattedText? _cachedSub;
    private Typeface? _cachedTypeface;

    private string? _cacheText;
    private string? _cacheTranslation;
    private string? _cacheFontFamily;
    private string? _cacheFontWeight;
    private double _cacheFontSize;
    private double _cacheTranslationSize;
    private double _cacheLetterSpacing;
    private double _cacheDpi;

    /// <summary>Glyph advance of each drawn character, scaled to the laid-out line width.</summary>
    private double[]? _charAdvances;

    /// <summary>Running totals of <see cref="_charAdvances"/>; one entry longer.</summary>
    private double[]? _charPrefix;

    /// <summary>Glyph advance of each syllable of the cached line, same scaling.</summary>
    private double[]? _syllableAdvances;
    private IReadOnlyList<LyricSyllable>? _syllableSource;
    private string[]? _syllableTexts;

    /// <summary>
    /// Memoised glyph advances. Measuring one costs a FormattedText construction, which is far
    /// too expensive to repeat per character per frame; the key covers everything the advance
    /// depends on. A lyric sheet only ever uses a few hundred distinct characters, so this
    /// saturates quickly and then costs a dictionary lookup.
    /// </summary>
    private static readonly Dictionary<(char Ch, double Size, string Family, FontWeight Weight, double Dpi), double>
        CharAdvanceCache = new();

    /// <summary>Same idea for whole runs (syllables), which are matched by their text.</summary>
    private static readonly Dictionary<(string Text, double Size, string Family, FontWeight Weight, double Dpi), double>
        RunAdvanceCache = new();

    /// <summary>Bound on <see cref="RunAdvanceCache"/>: one entry per distinct lyric run.</summary>
    private const int RunAdvanceCacheLimit = 4096;

    /// <summary>Last text written to the diag log, so it is reported once per line.</summary>
    private string? _diagText;

    /// <summary>Restart the marquee from the beginning whenever the text changes.</summary>
    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is KaraokeLine line)
        {
            line._scrollOffset = 0;
            line._scrollHoldMs = ScrollInitialHoldMs;
        }
    }

    private void EnsureScrollTimer()
    {
        if (_scrollTimer is not null) return;

        _scrollTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(ScrollFrameMs),
        };

        _scrollTimer.Tick += (_, _) => AdvanceScroll();
        _scrollTimer.Start();
    }

    /// <summary>
    /// Step the marquee. Does nothing at all when the text fits, so a normal-width
    /// strip pays no per-frame cost.
    /// </summary>
    private void AdvanceScroll()
    {
        if (!IsVisible) return;

        var overflow = _measuredTextWidth - Math.Max(0, ActualWidth - ContentInset);
        if (overflow <= 1)
        {
            if (_scrollOffset != 0)
            {
                _scrollOffset = 0;
                InvalidateVisual();
            }
            return;
        }

        // While the sweep is advancing, the follow (see FollowSweep) owns the offset: the line
        // moves with the voice rather than at a fixed speed. The marquee is what brings the rest
        // of a long line into view when nothing is being sung - paused playback, an interlude -
        // where nothing else would.
        if (_singing) return;

        // Hold at the start of a line so it can be read before it moves.
        if (_scrollHoldMs > 0)
        {
            _scrollHoldMs -= ScrollFrameMs;
            return;
        }

        _scrollOffset += ScrollSpeed * ScrollFrameMs / 1000.0;
        if (_scrollOffset >= overflow + ScrollRepeatGap)
        {
            _scrollOffset = 0;
            _scrollHoldMs = ScrollInitialHoldMs;
        }

        InvalidateVisual();
    }

    /// <summary>
    /// Steer the horizontal offset so that the sweep edge stays readable (P2).
    /// <para>
    /// A line wider than the room used to run as a constant-speed marquee, which knows nothing
    /// about where the singing is. Now, while the line is being sung, the offset is driven by the
    /// sung edge: the target holds that edge at <see cref="FollowFraction"/> of the room, so the
    /// words coming up stay visible to its right, and the offset approaches the target
    /// exponentially (<see cref="FollowRate"/>) instead of jumping - which is what makes a long
    /// line look like it is being carried along by the voice.
    /// </para>
    /// <para>
    /// Called from the drawing pass, which runs every frame while a line is playing, so the delta
    /// is a real frame time. A backwards jump in progress (seek, or a new line) restarts at the
    /// left edge.
    /// </para>
    /// </summary>
    private void FollowSweep(bool scrolling, double room, double sungWidth, double progress)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = _followTicks == 0 ? 0.0 : (now - _followTicks) / (double)Stopwatch.Frequency;
        _followTicks = now;
        if (dt < 0 || dt > 0.5) dt = 0.0;   // first pass, or a stall: do not jump

        if (!scrolling)
        {
            _scrollOffset = 0;
            _lastProgress = progress;
            _singing = false;
            return;
        }

        var previous = _lastProgress;
        _lastProgress = progress;

        // A new line, the same line sung again, or a seek backwards: start from the left edge.
        if (double.IsNaN(previous) || progress < previous - 0.001)
        {
            _scrollOffset = 0;
            _singing = false;
            return;
        }

        _singing = progress > previous + 0.0001;
        if (!_singing)
        {
            // Stalled: the marquee timer steps the offset instead, so the reader can still
            // reach the end of a long line while nothing is being sung.
            return;
        }

        var maxOffset = Math.Max(0, _measuredTextWidth - room);
        var target = Math.Clamp(sungWidth - room * FollowFraction, 0, maxOffset);
        var delta = target - _scrollOffset;

        if (Math.Abs(delta) < FollowSnapPx)
        {
            _scrollOffset = target;
            return;
        }

        _scrollOffset += delta * (dt > 0 ? 1 - Math.Exp(-FollowRate * dt) : 1);

        // Reported every few pixels of travel rather than per frame: the interpolation is built
        // at the call site, so a per-frame line would cost more than the follow itself.
        if (Diag.Enabled && Math.Abs(_scrollOffset - _diagFollowOffset) > 4)
        {
            _diagFollowOffset = _scrollOffset;
            Diag.Log($"[karaoke] follow offset={_scrollOffset:F1} target={target:F1} sung={sungWidth:F1} " +
                     $"room={room:F1} max={maxOffset:F1} text={_measuredTextWidth:F1} progress={progress:F3}");
        }
    }

    /// <summary>Stop the timer; called when the control leaves the tree.</summary>
    private void StopScrollTimer()
    {
        _scrollTimer?.Stop();
        _scrollTimer = null;
    }

    /// <summary>
    /// DIP reserved at the left edge for the hover cluster (cover art, title, artist, transport).
    /// The text is laid out inside the room that is left instead of being clipped against it, so
    /// no glyph is ever sliced in half; the marquee scrolls within that same room. The overlay
    /// animates this while the cluster fades in and out. It only affects rendering, never layout,
    /// so animating it re-draws the line without re-measuring the strip.
    /// </summary>
    public static readonly DependencyProperty ContentInsetProperty =
        DependencyProperty.Register(nameof(ContentInset), typeof(double), typeof(KaraokeLine),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double ContentInset
    {
        get => (double)GetValue(ContentInsetProperty);
        set => SetValue(ContentInsetProperty, value);
    }

    public static readonly DependencyProperty HighlightColorProperty =
        DependencyProperty.Register(nameof(HighlightColor), typeof(Brush), typeof(KaraokeLine),
            new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaseColorProperty =
        DependencyProperty.Register(nameof(BaseColor), typeof(Brush), typeof(KaraokeLine),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ContextColorProperty =
        DependencyProperty.Register(nameof(ContextColor), typeof(Brush), typeof(KaraokeLine),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyNameProperty =
        DependencyProperty.Register(nameof(FontFamilyName), typeof(string), typeof(KaraokeLine),
            new FrameworkPropertyMetadata("Microsoft YaHei UI", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeValueProperty =
        DependencyProperty.Register(nameof(FontSizeValue), typeof(double), typeof(KaraokeLine),
            new FrameworkPropertyMetadata(13.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TranslationFontSizeValueProperty =
        DependencyProperty.Register(nameof(TranslationFontSizeValue), typeof(double), typeof(KaraokeLine),
            new FrameworkPropertyMetadata(11.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Typeface weight name: <c>Normal</c>, <c>SemiBold</c> or <c>Bold</c>.
    /// Heavier weights stay legible over bright, busy wallpapers.
    /// </summary>
    public static readonly DependencyProperty FontWeightNameProperty =
        DependencyProperty.Register(nameof(FontWeightName), typeof(string), typeof(KaraokeLine),
            new FrameworkPropertyMetadata("SemiBold", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Extra spacing between characters, in DIP.</summary>
    public static readonly DependencyProperty LetterSpacingProperty =
        DependencyProperty.Register(nameof(LetterSpacing), typeof(double), typeof(KaraokeLine),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WordHighlightEnabledProperty =
        DependencyProperty.Register(nameof(WordHighlightEnabled), typeof(bool), typeof(KaraokeLine),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Draw the dark offset copy underneath the glyphs.
    /// <para>
    /// Worth it only for light text over an unpredictably dark backdrop. When the
    /// palette is chosen for contrast against a sampled taskbar colour, the shadow
    /// adds visible outline without adding legibility, and the reference
    /// implementation's taskbar widget draws its lyrics with no shadow at all.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty ShadowEnabledProperty =
        DependencyProperty.Register(nameof(ShadowEnabled), typeof(bool), typeof(KaraokeLine),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Syllable timings for the line, used for accurate word sweep.</summary>
    public IReadOnlyList<LyricSyllable>? Syllables { get; set; }

    /// <summary>Line start/end, used to interpolate when syllable timing is absent.</summary>
    public TimeSpan LineStart { get; set; }
    public TimeSpan LineEnd { get; set; }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Translation
    {
        get => (string?)GetValue(TranslationProperty);
        set => SetValue(TranslationProperty, value);
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public bool IsCurrent
    {
        get => (bool)GetValue(IsCurrentProperty);
        set => SetValue(IsCurrentProperty, value);
    }

    public Brush HighlightColor
    {
        get => (Brush)GetValue(HighlightColorProperty);
        set => SetValue(HighlightColorProperty, value);
    }

    public Brush BaseColor
    {
        get => (Brush)GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    public Brush ContextColor
    {
        get => (Brush)GetValue(ContextColorProperty);
        set => SetValue(ContextColorProperty, value);
    }

    public string FontFamilyName
    {
        get => (string)GetValue(FontFamilyNameProperty);
        set => SetValue(FontFamilyNameProperty, value);
    }

    public double FontSizeValue
    {
        get => (double)GetValue(FontSizeValueProperty);
        set => SetValue(FontSizeValueProperty, value);
    }

    public double TranslationFontSizeValue
    {
        get => (double)GetValue(TranslationFontSizeValueProperty);
        set => SetValue(TranslationFontSizeValueProperty, value);
    }

    public bool WordHighlightEnabled
    {
        get => (bool)GetValue(WordHighlightEnabledProperty);
        set => SetValue(WordHighlightEnabledProperty, value);
    }

    public bool ShadowEnabled
    {
        get => (bool)GetValue(ShadowEnabledProperty);
        set => SetValue(ShadowEnabledProperty, value);
    }

    public string FontWeightName
    {
        get => (string)GetValue(FontWeightNameProperty);
        set => SetValue(FontWeightNameProperty, value);
    }

    public double LetterSpacing
    {
        get => (double)GetValue(LetterSpacingProperty);
        set => SetValue(LetterSpacingProperty, value);
    }

    /// <summary>Map the configured weight name onto a WPF weight.</summary>
    private FontWeight ResolveWeight() => FontWeightName switch
    {
        "Normal" => FontWeights.Normal,
        "Bold" => FontWeights.Bold,
        "Light" => FontWeights.Light,
        _ => FontWeights.SemiBold,
    };

    /// <summary>
    /// Widen text to simulate letter spacing. A thin space is inserted between
    /// characters; at lyric font sizes this reads as natural tracking and is
    /// stable for CJK and Latin alike.
    /// </summary>
    private string ApplyLetterSpacing(string text)
    {
        var spacing = LetterSpacing;
        if (spacing <= 0.01 || text.Length < 2) return text;

        var sb = new System.Text.StringBuilder(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            sb.Append(text[i]);
            if (i < text.Length - 1) sb.Append('\u2009');
        }

        return sb.ToString();
    }

    private Typeface ResolveTypeface()
    {
        var weight = ResolveWeight();
        var name = FontFamilyName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            try
            {
                return new Typeface(new FontFamily(name), FontStyles.Normal,
                    weight, FontStretches.Normal);
            }
            catch
            {
                // Unknown font name: fall through to the safe default.
            }
        }
        return FallbackTypeface;
    }

    private static string DescribeBrush(Brush? brush) => brush switch
    {
        null => "null",
        SolidColorBrush s => s.Color.ToString(),
        _ => brush.GetType().Name,
    };

    /// <summary>
    /// Make the drawn text hit-testable.
    /// <para>
    /// A <see cref="FrameworkElement"/> that only overrides <c>OnRender</c> has no
    /// hit geometry, so the overlay could not tell "cursor is over the lyrics"
    /// from "cursor is over empty space". Returning a hit for the text bounds is
    /// what lets a click on the lyrics be handled by us (so it can be dragged)
    /// while clicks on the surrounding strip fall through to the taskbar.
    /// </para>
    /// </summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var point = hitTestParameters.HitPoint;
        if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight)
        {
            return null;
        }

        return new PointHitTestResult(this, point);
    }

    /// <summary>Total vertical space the element wants, including the translation row.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Text ?? string.Empty;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Must match the drawing pass exactly, including letter spacing, so the layout comes
        // from the same cache the render pass uses; laying out here warms it for free.
        var translation = string.IsNullOrEmpty(Translation) || Translation == text
            ? null
            : Translation;

        EnsureTextCache(text, translation, dpi);

        var main = _cachedMain!;
        double height = main.Height;

        if (_cachedSub is { } sub)
        {
            height += sub.Height + 1;
        }

        var width = double.IsInfinity(availableSize.Width) ? main.Width : availableSize.Width;
        return new Size(width, Math.Max(height, main.Height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        try
        {
            OnRenderCore(dc);
        }
        catch (Exception ex)
        {
            Diag.Log($"[karaoke] OnRender EXCEPTION {ex}");
        }
    }

    private void OnRenderCore(DrawingContext dc)
    {
        var text = Text;
        if (string.IsNullOrEmpty(text))
        {
            if (Diag.Enabled) Diag.Log($"[karaoke] OnRender skipped: empty text (isCurrent={IsCurrent})");
            return;
        }

        // The interpolated string is formatted at the call site, so the guard inside
        // Diag.Log is not enough to keep this off the render path. Logged on line changes
        // rather than per frame: a per-frame line is both useless and expensive enough to
        // distort the very measurements the log is used for.
        if (Diag.Enabled && _diagText != text)
        {
            _diagText = text;
            Diag.Log($"[karaoke] OnRender text='{text}' isCurrent={IsCurrent} " +
                     $"progress={Progress:F3} actualW={ActualWidth:F1} actualH={ActualHeight:F1} " +
                     $"base={DescribeBrush(BaseColor)} ctx={DescribeBrush(ContextColor)}");
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var hasTranslation = !string.IsNullOrEmpty(Translation) && Translation != text;
        var subText = hasTranslation ? Translation : null;

        // Layout, glyph advances and the letter-spaced run texts all come from the cache;
        // only the colours change from frame to frame.
        EnsureTextCache(text, subText, dpi);
        var main = _cachedMain!;
        var sub = _cachedSub;

        var totalHeight = main.Height + (sub is null ? 0 : sub.Height + 1);
        var top = Math.Max(0, (ActualHeight - totalHeight) / 2.0);

        // Remembered so the marquee timer can tell whether scrolling is needed.
        _measuredTextWidth = main.Width;

        // Horizontal placement inside the room that the hover cluster leaves behind:
        //   fits      -> centred in that room, so showing the cluster moves the words right
        //                without ever cutting a glyph off the front of the line
        //   overflows -> starts at the room's left edge and scrolls
        var room = Math.Max(0, ActualWidth - ContentInset);
        var scrolling = main.Width - room > 1;

        // The sweep edge is measured before the offset is chosen, because the follow steers by
        // it; the highlight pass below reuses this value instead of measuring a second time.
        var progress = Math.Clamp(Progress, 0, 1);
        var sungWidth = IsCurrent && WordHighlightEnabled ? MeasureSungWidth(main, progress) : 0.0;
        FollowSweep(scrolling, room, sungWidth, progress);

        // Each row is centred on its own width. Deriving the translation's position from
        // the original line's width left it aligned to that line's left edge, which reads
        // as left-shifted whenever the translation is the shorter of the two.
        double LeftFor(FormattedText text) =>
            scrolling ? ContentInset - _scrollOffset : ContentInset + (room - text.Width) / 2.0;

        var left = LeftFor(main);
        var subLeft = sub is null ? left : LeftFor(sub);

        // Clip to the control so an in-flight scroll can never paint outside the
        // lyric column; while the line is scrolling, the room is the visible window,
        // so the part that has scrolled past its left edge is not painted behind the
        // hover cluster.
        dc.PushClip(new RectangleGeometry(scrolling
            ? new Rect(ContentInset, 0, room, ActualHeight)
            : new Rect(0, 0, ActualWidth, ActualHeight)));

        try
        {
            // Non-current lines: flat colour, no sweep, no shadow (they are context).
            if (!IsCurrent)
            {
                DrawAt(dc, main, left, top, ContextColor);

                if (sub is not null)
                {
                    DrawAt(dc, sub, subLeft, top + main.Height + 1, ContextColor);
                }

                return;
            }

            if (!WordHighlightEnabled)
            {
                // Progress expressed as a whole-line colour switch; keeps the line
                // readable without a sweep.
                var brush = progress >= 1 ? HighlightColor : BaseColor;
                DrawAt(dc, main, left, top, brush);

                if (sub is not null)
                {
                    DrawAt(dc, sub, subLeft, top + main.Height + 1, ContextColor);
                }

                return;
            }

            // --- base pass (unsung colour) -----------------------------------
            DrawAt(dc, main, left, top, BaseColor);

            // --- highlight pass, clipped to the sung width -------------------
            if (sungWidth > 0.01)
            {
                // The clip starts where the text does, so it follows the scroll.
                var clip = new RectangleGeometry(new Rect(left, 0, sungWidth, ActualHeight));

                // Soft leading edge (P3): the last fraction of a character fades in rather than
                // ending on a hard vertical seam, so the sweep reads as a glow travelling through
                // the line. The mask is absolute-mapped, i.e. in the same DIP space the text is
                // drawn in, and padded either side, so everything behind the edge stays opaque.
                var fade = Math.Min(FontSizeValue * HighlightFadeRatio, sungWidth);
                if (fade > 0.5)
                {
                    var mask = new LinearGradientBrush
                    {
                        MappingMode = BrushMappingMode.Absolute,
                        SpreadMethod = GradientSpreadMethod.Pad,
                        StartPoint = new Point(left + sungWidth - fade, 0),
                        EndPoint = new Point(left + sungWidth, 0),
                    };
                    mask.GradientStops.Add(new GradientStop(Colors.White, 0));
                    mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
                    mask.Freeze();

                    dc.PushOpacityMask(mask);
                    dc.PushClip(clip);
                    DrawAt(dc, main, left, top, HighlightColor);
                    dc.Pop();
                    dc.Pop();
                }
                else
                {
                    dc.PushClip(clip);
                    DrawAt(dc, main, left, top, HighlightColor);
                    dc.Pop();
                }
            }

            // --- translation row ---------------------------------------------
            if (sub is not null)
            {
                // Shares the highlight once the line is under way, and scrolls with
                // the original so the two rows stay aligned.
                var translationBrush = progress > 0.02 ? HighlightColor : BaseColor;
                DrawAt(dc, sub, subLeft, top + main.Height + 1, translationBrush);
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    /// <summary>
    /// True when the cached layout still describes the requested line, typeface and DPI.
    /// Compared field by field: the key would otherwise have to be a string, and building
    /// one per frame is exactly the kind of garbage this cache exists to avoid.
    /// </summary>
    private bool TextCacheMatches(string text, string? translation, double dpi) =>
        _cachedMain is not null
        && _cacheText == text
        && _cacheTranslation == translation
        && _cacheDpi == dpi
        && _cacheFontFamily == FontFamilyName
        && _cacheFontWeight == FontWeightName
        && _cacheFontSize == FontSizeValue
        && _cacheTranslationSize == TranslationFontSizeValue
        && _cacheLetterSpacing == LetterSpacing;

    /// <summary>
    /// Build the layout objects and glyph advances for one line. Runs only when the line,
    /// the font settings or the DPI changed.
    /// </summary>
    private void EnsureTextCache(string text, string? translation, double dpi)
    {
        if (TextCacheMatches(text, translation, dpi)) return;

        var typeface = ResolveTypeface();
        var mainText = ApplyLetterSpacing(text);
        var subText = translation is null ? null : ApplyLetterSpacing(translation);

        _cachedMain = BuildText(mainText, typeface, FontSizeValue, Brushes.White, dpi);
        _cachedSub = subText is null
            ? null
            : BuildText(subText, typeface, TranslationFontSizeValue, Brushes.White, dpi);
        _cachedTypeface = typeface;

        _cacheText = text;
        _cacheTranslation = translation;
        _cacheDpi = dpi;
        _cacheFontFamily = FontFamilyName;
        _cacheFontWeight = FontWeightName;
        _cacheFontSize = FontSizeValue;
        _cacheTranslationSize = TranslationFontSizeValue;
        _cacheLetterSpacing = LetterSpacing;

        BuildCharAdvances(mainText, typeface, FontSizeValue, dpi, _cachedMain.Width);

        // Proves which face actually resolved: TryGetGlyphTypeface fails (or names a
        // fallback family) when the requested family is not available, which is the
        // difference between "MiSans rendered" and "silently fell back to SimSun".
        if (Diag.Enabled && typeface.TryGetGlyphTypeface(out var glyphs))
        {
            Diag.Log($"[karaoke] font source={typeface.FontFamily.Source} " +
                     $"face={string.Join("/", glyphs.FamilyNames.Values.Take(2))} weight={typeface.Weight} " +
                     $"chars={mainText.Length} width={_cachedMain.Width:F1}");
        }
    }

    /// <summary>
    /// Measure each character of the line once and keep both the advances and their running
    /// totals, so the per-frame sweep becomes an array read instead of a measurement pass.
    /// </summary>
    private void BuildCharAdvances(
        string mainText, Typeface typeface, double fontSize, double dpi, double lineWidth)
    {
        var count = mainText.Length;
        var advances = new double[count];
        var prefix = new double[count + 1];
        double sum = 0;

        for (int i = 0; i < count; i++)
        {
            var w = MeasureCharAdvance(mainText[i], typeface, fontSize, dpi);
            advances[i] = w;
            sum += w;
        }

        // Shaping (kerning, ligatures, the thin spaces used for letter spacing) means the sum
        // of isolated advances is never exactly the laid-out line width. Scaling by that ratio
        // keeps the sweep landing on the line's true right edge at progress = 1 while
        // preserving each character's share of the progress.
        var scale = sum > 0.01 ? lineWidth / sum : 1.0;
        for (int i = 0; i < count; i++)
        {
            advances[i] *= scale;
            prefix[i + 1] = prefix[i] + advances[i];
        }

        if (sum > 0.01) prefix[count] = lineWidth;

        _charAdvances = advances;
        _charPrefix = prefix;
    }

    /// <summary>
    /// Advances for the cached line's syllables, aligned with the syllable list. The list is
    /// set from outside and is not a dependency property, so it is validated by identity and
    /// by its texts rather than trusted because the line text matched.
    /// </summary>
    private double[] EnsureSyllableAdvances(IReadOnlyList<LyricSyllable> syllables, double lineWidth)
    {
        if (_syllableAdvances is not null && SyllableCacheMatches(syllables))
        {
            return _syllableAdvances;
        }

        var typeface = _cachedTypeface ?? FallbackTypeface;
        var count = syllables.Count;
        var advances = new double[count];
        var texts = new string[count];
        double sum = 0;

        for (int i = 0; i < count; i++)
        {
            texts[i] = syllables[i].Text;
            advances[i] = MeasureRunAdvance(texts[i], typeface, _cacheFontSize, _cacheDpi);
            sum += advances[i];
        }

        // Same normalisation as the per-character path.
        if (sum > 0.01)
        {
            var scale = lineWidth / sum;
            for (int i = 0; i < count; i++)
            {
                advances[i] *= scale;
            }
        }

        _syllableAdvances = advances;
        _syllableSource = syllables;
        _syllableTexts = texts;
        return advances;
    }

    private bool SyllableCacheMatches(IReadOnlyList<LyricSyllable> syllables)
    {
        if (!ReferenceEquals(_syllableSource, syllables)) return false;

        var texts = _syllableTexts;
        if (texts is null || texts.Length != syllables.Count) return false;

        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i] != syllables[i].Text) return false;
        }

        return true;
    }

    /// <summary>
    /// Width of the sung portion, in device-independent pixels, measured along
    /// the real glyph advances of the base text.
    /// </summary>
    private double MeasureSungWidth(FormattedText main, double progress)
    {
        if (progress >= 1) return main.Width;

        // Real syllable timing gives exact per-word boundaries.
        var syllables = Syllables;
        if (syllables is { Count: > 0 } && LineEnd > LineStart)
        {
            // Advances come from the cache, so this loop allocates nothing.
            var advances = EnsureSyllableAdvances(syllables, main.Width);
            var elapsed = (LineEnd - LineStart) * progress + LineStart;
            double acc = 0;
            double sung = 0;

            for (int i = 0; i < syllables.Count; i++)
            {
                var w = advances[i];
                var syl = syllables[i];
                if (elapsed >= syl.End)
                {
                    // Fully sung.
                    sung = acc + w;
                }
                else if (elapsed > syl.Start)
                {
                    // Partially sung.
                    var local = (elapsed - syl.Start).TotalMilliseconds /
                                Math.Max(1, syl.Duration.TotalMilliseconds);
                    sung = acc + (w * Math.Clamp(local, 0, 1));
                    break;
                }
                else
                {
                    break;
                }
                acc += w;
            }

            if (sung > 0) return Math.Min(sung, main.Width);
        }

        // No syllable data: distribute the sweep across actual character widths
        // rather than a single uniform fraction of the total. This makes the sweep
        // hit real glyph boundaries — CJK characters (wider glyphs) get
        // proportionally more sweep time, Latin characters less — which better
        // matches how each character is sung.
        return MeasureSungWidthByChar(main, progress);
    }

    /// <summary>
    /// Map progress (0..1) to a pixel width from the cached per-character advances.
    /// Unlike <c>main.Width * progress</c> (uniform fraction of total), this makes each
    /// character "light up" at its real glyph boundary: a wide CJK character takes a
    /// proportionally larger slice of the progress, so the sweep reads as
    /// character-by-character karaoke rather than a featureless wipe. Reading the running
    /// totals makes it O(1) per frame; it used to measure every character, every frame.
    /// </summary>
    private double MeasureSungWidthByChar(FormattedText main, double progress)
    {
        var advances = _charAdvances;
        var prefix = _charPrefix;
        if (advances is null || prefix is null || advances.Length == 0)
        {
            return main.Width * Math.Clamp(progress, 0, 1);
        }

        if (progress <= 0) return 0;

        int charCount = advances.Length;

        // Each character gets an equal share of progress (1/charCount), and the
        // pixel width for that share is the character's actual glyph advance.
        // Within a character's share, the clip interpolates smoothly so the sweep
        // is continuous, not discrete.
        double targetChar = progress * charCount;
        int fullChars = (int)Math.Floor(targetChar);
        if (fullChars >= charCount) return prefix[charCount];

        double width = prefix[fullChars];
        var partial = targetChar - fullChars;
        if (partial > 0)
        {
            width += advances[fullChars] * partial;
        }

        return Math.Min(width, main.Width);
    }

    /// <summary>
    /// Advance width of one character, memoised. A miss costs a FormattedText construction,
    /// which is why nothing on the per-frame path may call this directly.
    /// </summary>
    private static double MeasureCharAdvance(char ch, Typeface typeface, double fontSize, double dpi)
    {
        var key = (ch, fontSize, typeface.FontFamily.Source, typeface.Weight, dpi);
        if (CharAdvanceCache.TryGetValue(key, out var cached)) return cached;

        var width = MeasureRunAdvance(ch.ToString(), typeface, fontSize, dpi);
        CharAdvanceCache[key] = width;
        return width;
    }

    private static double MeasureRunAdvance(string runText, Typeface typeface, double fontSize, double dpi)
    {
        if (string.IsNullOrEmpty(runText)) return 0;

        var key = (runText, fontSize, typeface.FontFamily.Source, typeface.Weight, dpi);
        if (RunAdvanceCache.TryGetValue(key, out var cached)) return cached;

        var ft = new FormattedText(runText, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, typeface, fontSize, Brushes.White, dpi);
        var width = ft.WidthIncludingTrailingWhitespace;

        // Bounded: one entry per distinct lyric run, so a long session cannot grow it forever.
        if (RunAdvanceCache.Count >= RunAdvanceCacheLimit) RunAdvanceCache.Clear();
        RunAdvanceCache[key] = width;

        return width;
    }

    private static FormattedText BuildText(
        string text, Typeface typeface, double fontSize, Brush brush, double dpi)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            brush,
            dpi)
        {
            TextAlignment = TextAlignment.Left,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return ft;
    }

    /// <summary>
    /// Draw text at the given origin, optionally with the dark offset copy
    /// underneath that keeps light glyphs apart from a busy backdrop.
    /// <para>
    /// Recolouring goes through <see cref="FormattedText.SetForegroundBrush"/>, which leaves
    /// the laid-out glyph runs untouched: the same object can be drawn once per pass. Each
    /// pass used to build a fresh <see cref="FormattedText"/> instead, which re-ran layout.
    /// </para>
    /// </summary>
    private void DrawAt(DrawingContext dc, FormattedText text, double left, double top, Brush brush)
    {
        var origin = new Point(left, top);

        // Cheap drop shadow: a dark offset copy drawn underneath.
        if (ShadowEnabled && brush is SolidColorBrush { Color.A: > 0 })
        {
            text.SetForegroundBrush(ShadowBrush);
            dc.DrawText(text, new Point(origin.X + 1, origin.Y + 1));
        }

        text.SetForegroundBrush(brush);
        dc.DrawText(text, origin);
    }
}
