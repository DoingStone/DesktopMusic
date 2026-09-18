using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskbarLyrics.App.Controls;

/// <summary>
/// A colour swatch that shows its hex value and opens a picker popup.
/// <para>
/// Exposes the chosen colour as a hex string so it round-trips straight into
/// <see cref="Configuration.AppSettings"/>, which stores colours as text.
/// </para>
/// </summary>
public partial class ColorField : UserControl
{
    /// <summary>Swatches offered in the popup: common lyrics-overlay choices.</summary>
    private static readonly string[] Palette =
    {
        // Row 1: high-contrast karaoke accents
        "#FF3ABEFF", "#FF00E5FF", "#FF7CFC00", "#FFFFD700",
        "#FFFF6B6B", "#FFFF69B4", "#FFB388FF", "#FFFFFFFF",
        // Row 2: text tones
        "#FFE8E8E8", "#FFCCCCCC", "#FF999999", "#FF666666",
        "#FF333333", "#FF000000", "#8CFFFFFF", "#66FFFFFF",
        // Row 3: overlay panel backgrounds (dark with varying alpha)
        "#00000000", "#40121212", "#80121212", "#B3121212",
        "#E6121212", "#B3000000", "#B31E1E1E", "#B32B2B2B",
    };

    public static readonly DependencyProperty ColorValueProperty =
        DependencyProperty.Register(nameof(ColorValue), typeof(string), typeof(ColorField),
            new FrameworkPropertyMetadata("#FFFFFFFF",
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnColorValueChanged));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(ColorField),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary>
    /// Raised after the user picks a colour, so callers can apply it live.
    /// Uses plain <see cref="EventArgs"/> because it is a CLR event, not a routed
    /// one — XAML handlers must therefore declare <c>EventArgs</c>.
    /// </summary>
    public event EventHandler? ColorChanged;

    private bool _building;

    public ColorField()
    {
        // Building the palette raises no value changes, but the flag keeps the
        // initial visual refresh from re-entering through the DP callback.
        _building = true;
        InitializeComponent();
        BuildPalette();
        UpdateVisuals();
        _building = false;
    }

    /// <summary>Selected colour as <c>#AARRGGBB</c>.</summary>
    public string ColorValue
    {
        get => (string)GetValue(ColorValueProperty);
        set => SetValue(ColorValueProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private static void OnColorValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColorField field && !field._building) field.UpdateVisuals();
    }

    /// <summary>
    /// Build the swatch row.
    /// <para>
    /// Uses <see cref="Border"/> rather than <see cref="Button"/>: a swatch's whole
    /// purpose is to show its own colour, and any ambient Button style can override
    /// the chrome fill. Plain borders have no template to fight and make the colour
    /// the single source of truth. Border brushes are frozen so the many swatches
    /// stay cheap to render.
    /// </para>
    /// </summary>
    private void BuildPalette()
    {
        foreach (var hex in Palette)
        {
            var fill = new SolidColorBrush(ParseOrTransparent(hex));
            fill.Freeze();

            var edge = new SolidColorBrush(Color.FromArgb(0x59, 0x70, 0x70, 0x70));
            edge.Freeze();

            var swatch = new Border
            {
                Width = 26,
                Height = 22,
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand,
                ToolTip = hex,
                Background = fill,
                BorderBrush = edge,
                BorderThickness = new Thickness(1),
                Tag = hex,
            };

            swatch.MouseLeftButtonDown += (_, e) =>
            {
                ColorValue = (string)swatch.Tag;
                Picker.IsOpen = false;
                ColorChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            };

            // Accent outline on hover, drawn as an overlay so the colour itself is
            // never replaced.
            swatch.MouseEnter += (_, _) =>
                swatch.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0));
            swatch.MouseLeave += (_, _) => swatch.BorderBrush = edge;

            SwatchGrid.Children.Add(swatch);
        }
    }

    private void UpdateVisuals()
    {
        var color = ParseOrTransparent(ColorValue);
        Swatch.Background = new SolidColorBrush(color);
        HexText.Text = ColorValue;
        if (!Picker.IsOpen) HexInput.Text = ColorValue;
    }

    private void OnOpenPicker(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        HexInput.Text = ColorValue;
        Picker.IsOpen = !Picker.IsOpen;
        if (Picker.IsOpen)
        {
            HexInput.Focus();
            HexInput.SelectAll();
        }
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnApplyHex(sender, e);
        else if (e.Key == Key.Escape) Picker.IsOpen = false;
    }

    private void OnApplyHex(object sender, RoutedEventArgs e)
    {
        var text = HexInput.Text.Trim();

        if (!TryParseColor(text, out _))
        {
            ErrorText.Text = "格式不正确，示例：#FF3ABEFF 或 #3ABEFF";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Normalise to #AARRGGBB so stored values are consistent.
        ColorValue = Normalize(text);
        Picker.IsOpen = false;
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Accept #RGB, #RRGGBB and #AARRGGBB, with or without the leading '#'.</summary>
    private static bool TryParseColor(string text, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (!value.StartsWith('#')) value = "#" + value;

        try
        {
            if (value.Length is 4 or 7 or 9)
            {
                color = (Color)ColorConverter.ConvertFromString(value);
                return true;
            }
        }
        catch
        {
            // Reported to the user as a format error.
        }

        return false;
    }

    private static Color ParseOrTransparent(string? value) =>
        value is not null && TryParseColor(value, out var c) ? c : Colors.Transparent;

    private static string Normalize(string text)
    {
        var value = text.Trim();
        if (!value.StartsWith('#')) value = "#" + value;

        var color = (Color)ColorConverter.ConvertFromString(value);
        return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
