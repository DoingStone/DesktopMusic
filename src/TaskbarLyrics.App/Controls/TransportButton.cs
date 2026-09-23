using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TaskbarLyrics.App.Controls;

/// <summary>
/// A transport control drawn the way the reference implementation draws its own: a flat
/// vector glyph tinted with the surface's own colour, plus — for play/pause only — a pill
/// behind it at 7% opacity that deepens to 12% under the pointer.
/// <para>
/// The reference's numbers are 32x32 hit areas holding 16px glyphs, a 48x32 pill holding a
/// 20px glyph, 10px between them, switching over 100 ms ease-out, and the glyph colour
/// going from 70% to 100% of black or white. Everything here keeps those ratios; the two
/// heights are 30 instead of 32 because that widget is exactly one 32px row tall while
/// this strip is a single taskbar-height band that also carries a progress row, and a
/// 32px pill would be clipped by the band it sits in.
/// </para>
/// <para>
/// There is deliberately nothing else: the reference has no hover plate behind prev/next,
/// no border and no shadow, because the glyph colour alone carries the contrast against
/// whatever is behind it. The halo the old text glyphs needed is gone with them.
/// </para>
/// </summary>
public sealed class TransportButton : Button
{
    /// <summary>The reference's <c>transition-duration: .1s ease-out</c>.</summary>
    private const int TransitionMs = 100;

    // Mutated in place rather than replaced, so an animating colour costs no allocation
    // per frame. Neither brush may be frozen for that to work.
    private readonly SolidColorBrush _iconBrush = new();
    private readonly SolidColorBrush _pillBrush = new();

    public TransportButton()
    {
        // A templated Button would draw its own chrome on top of what OnRender draws.
        // An empty template leaves the whole surface to us.
        Template = new ControlTemplate(typeof(TransportButton));

        Focusable = false;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _iconBrush.Color = IconColor;
        _pillBrush.Color = PillColor;
    }

    /// <summary>Glyph, authored against a square view box (see <see cref="IconViewBox"/>).</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(Geometry),
        typeof(TransportButton),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize),
        typeof(double),
        typeof(TransportButton),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Side of the square the glyph was authored in (16 for prev/next, 20 for play/pause).
    /// The glyph is scaled from this rather than from its own bounds: prev/next do not fill
    /// their view box, and scaling from bounds would make them jump in size when the icon
    /// is swapped.
    /// </summary>
    public static readonly DependencyProperty IconViewBoxProperty = DependencyProperty.Register(
        nameof(IconViewBox),
        typeof(double),
        typeof(TransportButton),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Corner radius of the pill behind the glyph; 0 draws no pill at all.</summary>
    public static readonly DependencyProperty PillCornerRadiusProperty = DependencyProperty.Register(
        nameof(PillCornerRadius),
        typeof(double),
        typeof(TransportButton),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconColorProperty = DependencyProperty.Register(
        nameof(IconColor),
        typeof(Color),
        typeof(TransportButton),
        new FrameworkPropertyMetadata(Color.FromArgb(0xB3, 0x00, 0x00, 0x00), OnPaletteChanged));

    public static readonly DependencyProperty IconHoverColorProperty = DependencyProperty.Register(
        nameof(IconHoverColor),
        typeof(Color),
        typeof(TransportButton),
        new FrameworkPropertyMetadata(Color.FromArgb(0xFF, 0x00, 0x00, 0x00), OnPaletteChanged));

    public static readonly DependencyProperty PillColorProperty = DependencyProperty.Register(
        nameof(PillColor),
        typeof(Color),
        typeof(TransportButton),
        new FrameworkPropertyMetadata(Color.FromArgb(0x12, 0x00, 0x00, 0x00), OnPaletteChanged));

    public static readonly DependencyProperty PillHoverColorProperty = DependencyProperty.Register(
        nameof(PillHoverColor),
        typeof(Color),
        typeof(TransportButton),
        new FrameworkPropertyMetadata(Color.FromArgb(0x1F, 0x00, 0x00, 0x00), OnPaletteChanged));

    /// <summary>
    /// Outline drawn behind the glyph, in view-box units, or null for none.
    /// <para>
    /// The reference never needs one: it always knows what surface it is on and picks the
    /// glyph colour to match. Here the surface colour is read from the taskbar, and when
    /// that read fails the ink falls back to white — which is invisible on a light bar. The
    /// outline is that fallback's safety net, drawn around the glyph only: an Effect on the
    /// button would halo the play/pause pill as well and turn a 7% fill into a glowing slab.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty HaloColorProperty = DependencyProperty.Register(
        nameof(HaloColor),
        typeof(Color?),
        typeof(TransportButton),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Width of the outline, in the glyph's own view-box units.</summary>
    public static readonly DependencyProperty HaloWidthProperty = DependencyProperty.Register(
        nameof(HaloWidth),
        typeof(double),
        typeof(TransportButton),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // The animated values: what is actually on screen right now.
    private static readonly DependencyProperty CurrentIconColorProperty = DependencyProperty.Register(
        nameof(CurrentIconColor),
        typeof(Color),
        typeof(TransportButton),
        new FrameworkPropertyMetadata(Color.FromArgb(0xB3, 0x00, 0x00, 0x00), OnCurrentIconColorChanged));

    private static readonly DependencyProperty CurrentPillColorProperty = DependencyProperty.Register(
        nameof(CurrentPillColor),
        typeof(Color),
        typeof(TransportButton),
        new FrameworkPropertyMetadata(Color.FromArgb(0x12, 0x00, 0x00, 0x00), OnCurrentPillColorChanged));

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double IconViewBox
    {
        get => (double)GetValue(IconViewBoxProperty);
        set => SetValue(IconViewBoxProperty, value);
    }

    public double PillCornerRadius
    {
        get => (double)GetValue(PillCornerRadiusProperty);
        set => SetValue(PillCornerRadiusProperty, value);
    }

    /// <summary>Glyph colour while the pointer is elsewhere (70% of the surface colour).</summary>
    public Color IconColor
    {
        get => (Color)GetValue(IconColorProperty);
        set => SetValue(IconColorProperty, value);
    }

    /// <summary>Glyph colour under the pointer (the surface colour in full).</summary>
    public Color IconHoverColor
    {
        get => (Color)GetValue(IconHoverColorProperty);
        set => SetValue(IconHoverColorProperty, value);
    }

    /// <summary>Pill fill while the pointer is elsewhere; only drawn when the pill is on.</summary>
    public Color PillColor
    {
        get => (Color)GetValue(PillColorProperty);
        set => SetValue(PillColorProperty, value);
    }

    /// <summary>Pill fill under the pointer.</summary>
    public Color PillHoverColor
    {
        get => (Color)GetValue(PillHoverColorProperty);
        set => SetValue(PillHoverColorProperty, value);
    }

    /// <summary>Outline behind the glyph, or null for none.</summary>
    public Color? HaloColor
    {
        get => (Color?)GetValue(HaloColorProperty);
        set => SetValue(HaloColorProperty, value);
    }

    /// <summary>Outline width, in the glyph's own view-box units.</summary>
    public double HaloWidth
    {
        get => (double)GetValue(HaloWidthProperty);
        set => SetValue(HaloWidthProperty, value);
    }

    private Color CurrentIconColor
    {
        get => (Color)GetValue(CurrentIconColorProperty);
        set => SetValue(CurrentIconColorProperty, value);
    }

    private Color CurrentPillColor
    {
        get => (Color)GetValue(CurrentPillColorProperty);
        set => SetValue(CurrentPillColorProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = RenderSize.Width;
        var height = RenderSize.Height;
        if (width <= 0 || height <= 0) return;

        // Hit testing follows what was drawn, so without a full-size rectangle the button
        // would only be clickable on the glyph itself and clicks beside it would fall
        // through to the taskbar. A transparent brush still hit-tests; a null one does not.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        if (PillCornerRadius > 0)
        {
            drawingContext.DrawRoundedRectangle(
                _pillBrush,
                null,
                new Rect(0, 0, width, height),
                PillCornerRadius,
                PillCornerRadius);
        }

        var icon = Icon;
        if (icon is null) return;

        var viewBox = IconViewBox > 0 ? IconViewBox : 16;
        var size = IconSize > 0 ? IconSize : viewBox;
        var scale = size / viewBox;

        // Pushed in this order because the last transform pushed is applied first: the
        // glyph is scaled about the view box origin, then centred in the button.
        drawingContext.PushTransform(new TranslateTransform((width - size) / 2, (height - size) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));

        if (HaloColor is { } halo && HaloWidth > 0)
        {
            var pen = new Pen(new SolidColorBrush(halo), HaloWidth / scale)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            pen.Freeze();
            drawingContext.DrawGeometry(null, pen, icon);
        }

        drawingContext.DrawGeometry(_iconBrush, null, icon);
        drawingContext.Pop();
        drawingContext.Pop();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        ApplyPalette(animate: true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ApplyPalette(animate: true);
    }

    /// <summary>
    /// Move the two colours to wherever the pointer state says they belong. Palette changes
    /// take the same path with the animation off, so re-colouring an already-hovered button
    /// (taskbar changed shade, settings reloaded) lands immediately instead of sliding from
    /// a colour that is no longer valid.
    /// </summary>
    private void ApplyPalette(bool animate)
    {
        var hovered = IsMouseOver;

        Animate(CurrentIconColorProperty, hovered ? IconHoverColor : IconColor, animate);
        Animate(CurrentPillColorProperty, hovered ? PillHoverColor : PillColor, animate);
    }

    private void Animate(DependencyProperty property, Color target, bool animate)
    {
        if (!animate)
        {
            // An animation holds its final value, so the base value is only reachable
            // once it has been cleared.
            BeginAnimation(property, null);
            SetValue(property, target);
            return;
        }

        BeginAnimation(
            property,
            new ColorAnimation(target, TimeSpan.FromMilliseconds(TransitionMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private static void OnPaletteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TransportButton)d).ApplyPalette(animate: false);

    private static void OnCurrentIconColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (TransportButton)d;
        button._iconBrush.Color = (Color)e.NewValue;
    }

    private static void OnCurrentPillColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (TransportButton)d;
        button._pillBrush.Color = (Color)e.NewValue;
    }
}
