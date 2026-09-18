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

    private double _scrollOffset;
    private double _scrollHoldMs;
    private double _measuredTextWidth;
    private DispatcherTimer? _scrollTimer;

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

        var overflow = _measuredTextWidth - ActualWidth;
        if (overflow <= 1)
        {
            if (_scrollOffset != 0)
            {
                _scrollOffset = 0;
                InvalidateVisual();
            }
            return;
        }

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

    /// <summary>Stop the timer; called when the control leaves the tree.</summary>
    private void StopScrollTimer()
    {
        _scrollTimer?.Stop();
        _scrollTimer = null;
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
        var typeface = ResolveTypeface();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Must match the drawing pass exactly, including letter spacing, or the
        // element would measure smaller than it draws.
        var main = BuildText(ApplyLetterSpacing(Text), typeface, FontSizeValue, Brushes.White, dpi);
        double height = main.Height;

        if (!string.IsNullOrEmpty(Translation) && Translation != Text)
        {
            var sub = BuildText(ApplyLetterSpacing(Translation!), typeface, TranslationFontSizeValue,
                Brushes.White, dpi);
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
            Diag.Log($"[karaoke] OnRender skipped: empty text (isCurrent={IsCurrent})");
            return;
        }

        Diag.Log($"[karaoke] OnRender text='{text}' isCurrent={IsCurrent} " +
                 $"progress={Progress:F3} actualW={ActualWidth:F1} actualH={ActualHeight:F1} " +
                 $"base={DescribeBrush(BaseColor)} ctx={DescribeBrush(ContextColor)}");

        var typeface = ResolveTypeface();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var hasTranslation = !string.IsNullOrEmpty(Translation) && Translation != text;

        // FormattedText has no letter-spacing property, so extra tracking is
        // applied by widening the run text itself. Both the measuring and the
        // drawing passes therefore need the same widened text.
        var mainText = ApplyLetterSpacing(text);
        var subText = hasTranslation ? ApplyLetterSpacing(Translation!) : null;

        var main = BuildText(mainText, typeface, FontSizeValue, Brushes.White, dpi);
        var sub = subText is null
            ? null
            : BuildText(subText, typeface, TranslationFontSizeValue, Brushes.White, dpi);

        var totalHeight = main.Height + (sub is null ? 0 : sub.Height + 1);
        var top = Math.Max(0, (ActualHeight - totalHeight) / 2.0);

        // Remembered so the marquee timer can tell whether scrolling is needed.
        _measuredTextWidth = main.Width;

        // Horizontal placement:
        //   fits      -> centred, as before
        //   overflows -> left-aligned and scrolled
        // Centring an over-wide line made it spill out of both sides and overlap the
        // transport buttons; scrolling keeps the whole line reachable instead.
        var overflow = main.Width - ActualWidth;
        var left = overflow > 1 ? -_scrollOffset : (ActualWidth - main.Width) / 2.0;

        // Clip to the control so an in-flight scroll can never paint outside the
        // lyric column.
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));

        try
        {
            // Non-current lines: flat colour, no sweep, no shadow (they are context).
            if (!IsCurrent)
            {
                DrawAt(dc, main, left, top, ContextColor, typeface, FontSizeValue, dpi);

                if (sub is not null)
                {
                    DrawAt(dc, sub, left, top + main.Height + 1, ContextColor,
                        typeface, TranslationFontSizeValue, dpi);
                }

                return;
            }

            var progress = Math.Clamp(Progress, 0, 1);

            if (!WordHighlightEnabled)
            {
                // Progress expressed as a whole-line colour switch; keeps the line
                // readable without a sweep.
                var brush = progress >= 1 ? HighlightColor : BaseColor;
                DrawAt(dc, main, left, top, brush, typeface, FontSizeValue, dpi);

                if (sub is not null)
                {
                    DrawAt(dc, sub, left, top + main.Height + 1, ContextColor,
                        typeface, TranslationFontSizeValue, dpi);
                }

                return;
            }

            // --- base pass (unsung colour) -----------------------------------
            DrawAt(dc, main, left, top, BaseColor, typeface, FontSizeValue, dpi);

            // --- highlight pass, clipped to the sung width -------------------
            var sungWidth = MeasureSungWidth(main, progress);
            if (sungWidth > 0.01)
            {
                // The clip starts where the text does, so it follows the scroll.
                dc.PushClip(new RectangleGeometry(new Rect(left, 0, sungWidth, ActualHeight)));
                DrawAt(dc, main, left, top, HighlightColor, typeface, FontSizeValue, dpi);
                dc.Pop();
            }

            // --- translation row ---------------------------------------------
            if (sub is not null)
            {
                // Shares the highlight once the line is under way, and scrolls with
                // the original so the two rows stay aligned.
                var translationBrush = progress > 0.02 ? HighlightColor : BaseColor;
                DrawAt(dc, sub, left, top + main.Height + 1, translationBrush,
                    typeface, TranslationFontSizeValue, dpi);
            }
        }
        finally
        {
            dc.Pop();
        }
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
            var elapsed = (LineEnd - LineStart) * progress + LineStart;
            double acc = 0;
            double sung = 0;

            var typeface = ResolveTypeface();
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            foreach (var syl in syllables)
            {
                var w = MeasureRunWidth(syl.Text, typeface, FontSizeValue, dpi);
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

        // No syllable data: advance the sweep proportionally across the rendered
        // width. For CJK lyrics one character is one advance, so this reads as a
        // per-character karaoke fill, which is the expected look.
        return Math.Min(main.Width * progress, main.Width);
    }

    private double MeasureRunWidth(string runText, Typeface typeface, double fontSize, double dpi)
    {
        if (string.IsNullOrEmpty(runText)) return 0;
        var ft = new FormattedText(runText, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, typeface, fontSize, Brushes.White, dpi);
        return ft.WidthIncludingTrailingWhitespace;
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
    /// Draw text horizontally centred, with a soft shadow so lyrics stay legible
    /// over a light taskbar background.
    /// </summary>
    private static void DrawAt(
        DrawingContext dc,
        FormattedText text,
        double left,
        double top,
        Brush brush,
        Typeface typeface,
        double fontSize,
        double dpi)
    {
        var origin = new Point(left, top);

        // Cheap drop shadow: a dark offset copy drawn underneath.
        if (brush is SolidColorBrush { Color.A: > 0 })
        {
            var shadow = new FormattedText(
                text.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                ShadowBrush,
                dpi);

            dc.DrawText(shadow, new Point(origin.X + 1, origin.Y + 1));
        }

        // Recolour by rebuilding with the target brush.
        var recoloured = new FormattedText(
            text.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            brush,
            dpi);

        dc.DrawText(recoloured, origin);
    }
}
