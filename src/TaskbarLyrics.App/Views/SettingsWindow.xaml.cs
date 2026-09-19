using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TaskbarLyrics.App.Configuration;

namespace TaskbarLyrics.App.Views;

/// <summary>
/// The settings UI. Edits a working copy of <see cref="AppSettings"/> and pushes
/// each change live to the overlay, so the taskbar shows the result immediately.
/// <para>
/// "保存" writes to disk; "取消" restores the snapshot taken at open time.
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    private readonly Action<AppSettings> _applyLive;
    private readonly Action<AppSettings> _save;
    private readonly Action? _rematch;
    private readonly Action? _clearCache;
    private readonly Action? _showDiagnostics;
    private readonly Action? _quit;
    private readonly Action? _transportPlayPause;
    private readonly Action? _transportNext;
    private readonly Action? _transportPrevious;

    private AppSettings _working = new();

    /// <summary>
    /// Guards against handler feedback while values are being loaded.
    /// <para>
    /// Starts <c>true</c> because loading the XAML itself assigns
    /// <c>Slider.Minimum</c>, which raises <c>ValueChanged</c> before the
    /// constructor body has run — at that point the field initialisers are the
    /// only thing that has executed.
    /// </para>
    /// </summary>
    private bool _loading = true;

    /// <summary>Guards hotkey text assignment from re-entering the read step.</summary>
    private bool _readingHotkeys;

    public SettingsWindow(
        AppSettings settings,
        Action<AppSettings> applyLive,
        Action<AppSettings> save,
        Action? rematch = null,
        Action? clearCache = null,
        Action? showDiagnostics = null,
        Action? quit = null,
        Action? transportPlayPause = null,
        Action? transportNext = null,
        Action? transportPrevious = null)
    {
        InitializeComponent();

        AppIcons.ApplyTo(this);

        // Fluent chrome. Mica must be requested once the handle exists and needs a
        // transparent window background to show through; unsupported systems fall
        // back to the solid Fluent grey.
        SourceInitialized += (_, _) =>
        {
            // Explicitly clear any inherited WS_EX_TOPMOST so this dialog never
            // stays above other applications — the overlay is topmost but the
            // settings window must be a normal, non-topmost window.
            Interop.NativeMethods.ClearTopmost(this);

            // Windows 11 chrome: rounded corners, light title bar tinted to the page,
            // and the solid Settings background. No Mica — it needs the client area
            // merged into the frame, and without that a transparent client area
            // renders black (the previous bug).
            Interop.FluentChrome.Apply(this, _working.Theme, _working.UseMica);
        };

        _original = settings.Clone();
        _working = settings.Clone();

        _applyLive = applyLive;
        _save = save;
        _rematch = rematch;
        _clearCache = clearCache;
        _showDiagnostics = showDiagnostics;
        _quit = quit;
        _transportPlayPause = transportPlayPause;
        _transportNext = transportNext;
        _transportPrevious = transportPrevious;

        ConfigPathText.Text = AppSettings.ConfigPath;

        PopulateFonts();
        PopulateFontWeights();
        LoadFromWorking();

        // Sidebar state. Selecting the first item also sets the page title and shows
        // the matching page, so this must run after the fields above are populated.
        AppIconImage.Source = AppIcons.Image;
        AboutVersionText.Text = $"任务栏歌词 {AppVersion}";
        AboutRepoText.Text = "https://github.com/DoingStone/DesktopMusic";
        NavList.SelectedIndex = 0;

        // Collapse state, theme and material live in the working copy, so a dialog with
        // unsaved changes still shows what the user last chose.
        ApplyNavCollapsed(_working.NavCollapsed, persist: false);

        ThemeCombo.SelectedIndex = _working.Theme switch
        {
            Interop.FluentChrome.ThemeLight => 1,
            Interop.FluentChrome.ThemeDark => 2,
            _ => 0,
        };
        MicaCheck.IsChecked = _working.UseMica;
        NavCollapsedCheck.IsChecked = _working.NavCollapsed;

        // XAML loading and the initial value push are done; user edits may now
        // apply live.
        _loading = false;
        ValidateHotkeys();
    }

    /// <summary>Window caption buttons for the merged title bar.</summary>
    private void OnMinimise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>Shown on the About page.</summary>
    private static string AppVersion =>
        typeof(SettingsWindow).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "1.0.0";

    /// <summary>
    /// Page metadata, indexed to match the order of the sidebar items in XAML. Keeping
    /// the two in one place means a page cannot be added without giving it a title.
    /// </summary>
    private (string Title, FrameworkElement Page)[] Pages => new[]
    {
        ("常规", (FrameworkElement)PageGeneral),
        ("外观", PageAppearance),
        ("位置与显示", PagePosition),
        ("歌词来源", PageLyrics),
        ("播放控制", PagePlayback),
        ("关于", PageAbout),
    };

    /// <summary>
    /// Collapse or expand the navigation pane, keeping the title row's filler in step so
    /// the app identity stays aligned with the content edge.
    /// </summary>
    private void OnToggleNav(object sender, RoutedEventArgs e) =>
        ApplyNavCollapsed(!_working.NavCollapsed, persist: true);

    private void ApplyNavCollapsed(bool collapsed, bool persist)
    {
        _working.NavCollapsed = collapsed;

        NavColumn.Width = collapsed ? new GridLength(0) : new GridLength(232);
        NavPane.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        TitleFiller.Margin = collapsed ? new Thickness(0) : new Thickness(232, 0, 0, 0);

        if (NavCollapsedCheck is not null) NavCollapsedCheck.IsChecked = collapsed;

        if (persist)
        {
            _applyLive(_working);
            SetStatus(collapsed ? "左侧栏已收起" : "左侧栏已展开");
        }
    }

    /// <summary>Theme changed: re-apply the window chrome immediately.</summary>
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        _working.Theme = ThemeCombo.SelectedIndex switch
        {
            1 => Interop.FluentChrome.ThemeLight,
            2 => Interop.FluentChrome.ThemeDark,
            _ => Interop.FluentChrome.ThemeSystem,
        };

        Interop.FluentChrome.Apply(this, _working.Theme, _working.UseMica);
        _applyLive(_working);
        SetStatus("主题已更新");
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = NavList.SelectedIndex;
        var pages = Pages;

        if (index < 0 || index >= pages.Length) return;

        for (int i = 0; i < pages.Length; i++)
        {
            pages[i].Page.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        PageTitle.Text = pages[index].Title;
    }

    /// <summary>Weights offered for the lyric text.</summary>
    private void PopulateFontWeights()
    {
        foreach (var weight in new[] { "Light", "Normal", "SemiBold", "Bold" })
        {
            FontWeightCombo.Items.Add(weight);
        }
    }

    /// <summary>Lazily create one window per app instance and reuse it.</summary>
    public static SettingsWindow? Current { get; private set; }

    /// <summary>Open (or focus) the singleton settings window.</summary>
    public static void ShowOrFocus(
        Window? owner,
        Func<AppSettings> currentSettings,
        Action<AppSettings> applyLive,
        Action<AppSettings> save,
        Action? rematch = null,
        Action? clearCache = null,
        Action? showDiagnostics = null,
        Action? quit = null,
        Action? transportPlayPause = null,
        Action? transportNext = null,
        Action? transportPrevious = null)
    {
        if (Current is { IsLoaded: true })
        {
            if (Current.WindowState == WindowState.Minimized)
            {
                Current.WindowState = WindowState.Normal;
            }

            // Ensure the window is not stuck as topmost from a prior activation.
            Interop.NativeMethods.ClearTopmost(Current);
            Current.Topmost = false;

            Current.Activate();
            return;
        }

        var window = new SettingsWindow(
            currentSettings(), applyLive, save, rematch, clearCache, showDiagnostics, quit,
            transportPlayPause, transportNext, transportPrevious);

        // Deliberately NOT owned by the overlay. The overlay is permanently
        // topmost, and an owned window inherits that band — which would pin this
        // dialog above every other application. A normal window that merely starts
        // centred behaves correctly.

        Current = window;

        window.Closed += (_, _) => Current = null;
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Common Chinese font families. Used to pin the likely choices to the top of
    /// the list, and to guarantee they appear even when enumeration reports them
    /// only under an English name.
    /// </summary>
    private static readonly string[] CommonChineseFonts =
    {
        "微软雅黑", "Microsoft YaHei", "等线", "DengXian",
        "宋体", "SimSun", "新宋体", "NSimSun", "黑体", "SimHei",
        "楷体", "KaiTi", "仿宋", "FangSong", "幼圆", "YouYuan", "隶书", "LiSu",
        "微软正黑体", "Microsoft JhengHei", "新細明體", "PMingLiU", "標楷體", "DFKai-SB",
        "华文细黑", "STXihei", "华文中宋", "STZhongsong", "华文楷体", "STKaiti",
        "华文宋体", "STSong", "华文仿宋", "STFangsong", "华文新魏", "STXinwei",
        "华文行楷", "STXingkai", "华文隶书", "STLiti", "华文琥珀", "STHupo",
        "方正舒体", "FZShuTi", "方正姚体", "FZYaoTi",
        "思源黑体", "Source Han Sans SC", "Noto Sans CJK SC",
        "HarmonyOS Sans SC", "MiSans", "OPPO Sans", "Alibaba PuHuiTi",
    };

    private void PopulateFonts()
    {
        // Group every family by all of its localised names. Taking just
        // FamilyNames.Values.First() returns an arbitrary entry — usually the
        // English one — which is why Chinese faces showed up as "Microsoft YaHei"
        // instead of 微软雅黑 and the list looked like it had no Chinese fonts.
        var byLocalisedName = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var family in Fonts.SystemFontFamilies)
        {
            var best = PreferredName(family);
            if (string.IsNullOrWhiteSpace(best)) continue;

            if (!display.ContainsKey(best)) display[best] = family.Source;

            foreach (var name in family.FamilyNames.Values)
            {
                if (!string.IsNullOrWhiteSpace(name)) byLocalisedName[name] = family;
            }

            byLocalisedName[family.Source] = family;
            byLocalisedName[best] = family;
        }

        var ordered = new List<string>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Chinese favourites first so they are immediately reachable; add both the
        // localised name and the canonical one when the family is installed.
        foreach (var candidate in CommonChineseFonts)
        {
            if (!byLocalisedName.TryGetValue(candidate, out var family)) continue;

            var name = display.TryGetValue(candidate, out var shown) ? shown : candidate;
            if (added.Add(name)) ordered.Add(name);

            if (added.Add(family.Source)) ordered.Add(family.Source);
        }

        // Then everything else, in the user's own culture so Chinese names sort
        // naturally rather than by code point.
        foreach (var name in display.Keys.OrderBy(n => n, StringComparer.CurrentCulture))
        {
            if (added.Add(name)) ordered.Add(name);
        }

        foreach (var name in ordered) FontCombo.Items.Add(name);

        // The configured font may not be enumerable (e.g. a font alias); keep it
        // selectable so opening settings never silently changes the font.
        if (!string.IsNullOrWhiteSpace(_working.FontFamily) &&
            !FontCombo.Items.Cast<string>().Contains(_working.FontFamily, StringComparer.OrdinalIgnoreCase))
        {
            FontCombo.Items.Insert(0, _working.FontFamily);
        }
    }

    /// <summary>
    /// The family's most useful display name: Chinese when the font offers one,
    /// otherwise its invariant name.
    /// </summary>
    private static string? PreferredName(FontFamily family)
    {
        foreach (var culture in new[] { "zh-CN", "zh-Hans", "zh-TW", "zh-Hant", "zh" })
        {
            if (family.FamilyNames.TryGetValue(
                    System.Windows.Markup.XmlLanguage.GetLanguage(culture), out var localised) &&
                !string.IsNullOrWhiteSpace(localised))
            {
                return localised;
            }
        }

        return family.FamilyNames.Values.FirstOrDefault() ?? family.Source;
    }

    // ---- loading / reading ---------------------------------------------

    private void LoadFromWorking()
    {
        _loading = true;
        try
        {
            FontCombo.SelectedItem = FontCombo.Items.Cast<string>()
                .FirstOrDefault(n => string.Equals(n, _working.FontFamily, StringComparison.OrdinalIgnoreCase))
                ?? _working.FontFamily;

            OriginalSizeSlider.Value = Clamp(_working.OriginalFontSize, OriginalSizeSlider.Minimum, OriginalSizeSlider.Maximum);
            TranslationSizeSlider.Value = Clamp(_working.TranslationFontSize, TranslationSizeSlider.Minimum, TranslationSizeSlider.Maximum);
            LetterSpacingSlider.Value = Clamp(_working.LetterSpacing, LetterSpacingSlider.Minimum, LetterSpacingSlider.Maximum);
            SelectFontWeight(_working.FontWeight);

            HighlightColorField.ColorValue = _working.HighlightColor;
            BaseColorField.ColorValue = _working.BaseColor;
            ContextColorField.ColorValue = _working.ContextColor;
            BackgroundColorField.ColorValue = _working.BackgroundColor;

            ShowBackgroundCheck.IsChecked = _working.ShowBackground;
            WordHighlightCheck.IsChecked = _working.EnableWordHighlight;
            ShowTranslationCheck.IsChecked = _working.ShowTranslation;
            ShowContextCheck.IsChecked = _working.ShowContextLines;
            CornerSlider.Value = Clamp(_working.BackgroundCornerRadius, CornerSlider.Minimum, CornerSlider.Maximum);

            WidthSlider.Value = Clamp(_working.Width, WidthSlider.Minimum, WidthSlider.Maximum);
            HeightSlider.Value = Clamp(_working.Height, HeightSlider.Minimum, HeightSlider.Maximum);
            VerticalAlignSlider.Value = Clamp(_working.VerticalAlign, VerticalAlignSlider.Minimum, VerticalAlignSlider.Maximum);
            OffsetXSlider.Value = Clamp(_working.OffsetX, OffsetXSlider.Minimum, OffsetXSlider.Maximum);
            OffsetYSlider.Value = Clamp(_working.OffsetY, OffsetYSlider.Minimum, OffsetYSlider.Maximum);
            OpacitySlider.Value = Clamp(_working.OverlayOpacity, OpacitySlider.Minimum, OpacitySlider.Maximum);

            InteractiveCheck.IsChecked = _working.Interactive;
            PlaceAboveCheck.IsChecked = _working.PlaceAboveTaskbar;
            TransportControlsCheck.IsChecked = _working.ShowTransportControls;
            SongProgressCheck.IsChecked = _working.ShowSongProgress;
            VisibleCheck.IsChecked = _working.Visible;
            ShowWhenPausedCheck.IsChecked = _working.ShowWhenPaused;
            HideWhenNoLyricsCheck.IsChecked = _working.HideWhenNoLyrics;
            OffsetSlider.Value = Clamp(_working.GlobalOffsetMs, OffsetSlider.Minimum, OffsetSlider.Maximum);

            HotkeyPlayPauseBox.Text = _working.HotkeyPlayPause;
            HotkeyNextBox.Text = _working.HotkeyNextTrack;
            HotkeyPreviousBox.Text = _working.HotkeyPreviousTrack;
            HotkeyToggleBox.Text = _working.HotkeyToggleOverlay;

            QqMusicCheck.IsChecked = _working.EnableQqMusic;
            NetEaseCheck.IsChecked = _working.EnableNetEase;
            LrclibCheck.IsChecked = _working.EnableLrclib;

            UpdateValueLabels();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Copy every control value into the working settings.</summary>
    private void ReadIntoWorking()
    {
        _working.FontFamily = FontCombo.SelectedItem as string ?? _working.FontFamily;

        _working.OriginalFontSize = OriginalSizeSlider.Value;
        _working.TranslationFontSize = TranslationSizeSlider.Value;
        _working.LetterSpacing = LetterSpacingSlider.Value;
        _working.FontWeight = FontWeightCombo.SelectedItem as string ?? _working.FontWeight;

        _working.HighlightColor = HighlightColorField.ColorValue;
        _working.BaseColor = BaseColorField.ColorValue;
        _working.ContextColor = ContextColorField.ColorValue;
        _working.BackgroundColor = BackgroundColorField.ColorValue;

        _working.ShowBackground = ShowBackgroundCheck.IsChecked == true;
        _working.EnableWordHighlight = WordHighlightCheck.IsChecked == true;
        _working.ShowTranslation = ShowTranslationCheck.IsChecked == true;
        _working.ShowContextLines = ShowContextCheck.IsChecked == true;
        _working.BackgroundCornerRadius = CornerSlider.Value;

        _working.Width = WidthSlider.Value;
        _working.Height = HeightSlider.Value;
        _working.VerticalAlign = VerticalAlignSlider.Value;
        _working.OffsetX = OffsetXSlider.Value;
        _working.OffsetY = OffsetYSlider.Value;
        _working.OverlayOpacity = OpacitySlider.Value;

        _working.Interactive = InteractiveCheck.IsChecked == true;
        _working.PlaceAboveTaskbar = PlaceAboveCheck.IsChecked == true;
        _working.ShowTransportControls = TransportControlsCheck.IsChecked == true;
        _working.ShowSongProgress = SongProgressCheck.IsChecked == true;
        _working.Visible = VisibleCheck.IsChecked == true;
        _working.ShowWhenPaused = ShowWhenPausedCheck.IsChecked == true;
        _working.HideWhenNoLyrics = HideWhenNoLyricsCheck.IsChecked == true;
        _working.UseMica = MicaCheck.IsChecked == true;
        _working.NavCollapsed = NavCollapsedCheck.IsChecked == true;
        _working.GlobalOffsetMs = (int)Math.Round(OffsetSlider.Value);

        // Guarded: assigning these texts raises TextChanged, which would re-enter
        // this method while it is still running.
        if (!_readingHotkeys)
        {
            _readingHotkeys = true;
            try
            {
                _working.HotkeyPlayPause = HotkeyPlayPauseBox.Text.Trim();
                _working.HotkeyNextTrack = HotkeyNextBox.Text.Trim();
                _working.HotkeyPreviousTrack = HotkeyPreviousBox.Text.Trim();
                _working.HotkeyToggleOverlay = HotkeyToggleBox.Text.Trim();
            }
            finally
            {
                _readingHotkeys = false;
            }
        }

        _working.EnableQqMusic = QqMusicCheck.IsChecked == true;
        _working.EnableNetEase = NetEaseCheck.IsChecked == true;
        _working.EnableLrclib = LrclibCheck.IsChecked == true;
    }

    /// <summary>Select the configured font weight, adding it if not in the list.</summary>
    private void SelectFontWeight(string? weight)
    {
        var wanted = string.IsNullOrWhiteSpace(weight) ? "SemiBold" : weight;

        if (!FontWeightCombo.Items.Cast<string>().Contains(wanted, StringComparer.OrdinalIgnoreCase))
        {
            FontWeightCombo.Items.Add(wanted);
        }

        FontWeightCombo.SelectedItem = FontWeightCombo.Items.Cast<string>()
            .FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase))
            ?? "SemiBold";
    }

    /// <summary>
    /// Called when any hotkey text changes. Reports whether the text parses, so a
    /// typo is visible before it silently does nothing.
    /// </summary>
    private void OnHotkeyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        ValidateHotkeys();
        ApplyLive();
    }

    private void ValidateHotkeys()
    {
        var problems = new List<string>();

        foreach (var (box, label) in new[]
        {
            (HotkeyPlayPauseBox, "暂停/播放"),
            (HotkeyNextBox, "下一首"),
            (HotkeyPreviousBox, "上一首"),
            (HotkeyToggleBox, "显示/隐藏歌词"),
        })
        {
            var text = box.Text.Trim();
            if (text.Length == 0) continue;   // empty disables the shortcut

            if (Interop.GlobalHotkeys.Parse(0, text) is null)
            {
                problems.Add($"{label}：「{text}」格式无法识别");
            }
        }

        HotkeyStatusText.Text = problems.Count == 0
            ? "格式均有效。保存后立即生效。"
            : string.Join("；", problems);
    }

    // ---- transport commands --------------------------------------------

    private void OnTransportPlayPause(object sender, RoutedEventArgs e) =>
        _transportPlayPause?.Invoke();

    private void OnTransportNext(object sender, RoutedEventArgs e) =>
        _transportNext?.Invoke();

    private void OnTransportPrevious(object sender, RoutedEventArgs e) =>
        _transportPrevious?.Invoke();

    /// <summary>
    /// Adopt a position that was changed outside this dialog (a drag on the
    /// overlay).
    /// <para>
    /// The dialog edits a snapshot taken when it opened, so without this its stale
    /// offset would overwrite the drag on the next edit and snap the strip back to
    /// where it started. Keeping the working copy in step makes the drag
    /// authoritative.
    /// </para>
    /// </summary>
    public void AdoptPosition(double offsetX, double offsetY)
    {
        _working.OffsetX = offsetX;
        _working.OffsetY = offsetY;

        _loading = true;
        try
        {
            OffsetXSlider.Value = Clamp(offsetX, OffsetXSlider.Minimum, OffsetXSlider.Maximum);
            OffsetYSlider.Value = Clamp(offsetY, OffsetYSlider.Minimum, OffsetYSlider.Maximum);
        }
        finally
        {
            _loading = false;
        }

        UpdateValueLabels();
        SetStatus($"已记录拖动位置（X={offsetX:0}）");
    }

    private void UpdateValueLabels()
    {
        OriginalSizeText.Text = OriginalSizeSlider.Value.ToString("0.#");
        TranslationSizeText.Text = TranslationSizeSlider.Value.ToString("0.#");
        CornerText.Text = CornerSlider.Value.ToString("0");
        WidthText.Text = WidthSlider.Value.ToString("0");
        HeightText.Text = HeightSlider.Value <= 0 ? "自动" : HeightSlider.Value.ToString("0");
        VerticalAlignText.Text = VerticalAlignSlider.Value.ToString("0.0");
        OffsetXText.Text = OffsetXSlider.Value.ToString("+0;-0;0");
        OffsetYText.Text = OffsetYSlider.Value.ToString("+0;-0;0");
        OpacityText.Text = (OpacitySlider.Value * 100).ToString("0") + "%";
        LetterSpacingText.Text = LetterSpacingSlider.Value.ToString("0.#");
        OffsetText.Text = ((int)Math.Round(OffsetSlider.Value)).ToString("+0;-0;0") + " ms";
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;

    // ---- live apply ----------------------------------------------------

    /// <summary>Checkbox / toggle edits.</summary>
    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        ApplyLive();

        // Material and collapse state are properties of this window, so they have to be
        // pushed to it rather than only written to the settings file.
        Interop.FluentChrome.Apply(this, _working.Theme, _working.UseMica);

        if (NavCollapsedCheck is not null && NavCollapsedCheck.IsChecked != _working.NavCollapsed)
        {
            ApplyNavCollapsed(_working.NavCollapsed, persist: false);
        }
    }


    /// <summary>Colour picker edits (ColorField raises a plain CLR event).</summary>
    private void OnColorChanged(object? sender, EventArgs e) => ApplyLive();

    private void OnFontChanged(object sender, SelectionChangedEventArgs e) => ApplyLive();

    /// <summary>Slider edits.</summary>
    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyLive();

    private void OnProviderChanged(object sender, RoutedEventArgs e)
    {
        ReadIntoWorking();
        UpdateValueLabels();
        SetStatus("歌词来源已更改，重新匹配后生效");
    }

    private void ApplyLive()
    {
        // _loading is true while the XAML is still being parsed: Slider.Minimum
        // assignments raise ValueChanged before the constructor body runs.
        if (_loading || _working is null || _applyLive is null) return;

        ReadIntoWorking();
        UpdateValueLabels();
        _applyLive(_working);
        SetStatus("修改已即时生效（尚未保存）");
    }

    // ---- position nudging ----------------------------------------------

    private void Nudge(double dx, double dy)
    {
        _working.OffsetX += dx;
        _working.OffsetY += dy;
        _applyLive(_working);
        SetStatus($"位置偏移：X={_working.OffsetX:0} Y={_working.OffsetY:0}");
    }

    private void OnMoveLeft(object sender, RoutedEventArgs e) => Nudge(-10, 0);
    private void OnMoveRight(object sender, RoutedEventArgs e) => Nudge(+10, 0);
    private void OnMoveUp(object sender, RoutedEventArgs e) => Nudge(0, -3);
    private void OnMoveDown(object sender, RoutedEventArgs e) => Nudge(0, +3);

    private void OnResetPosition(object sender, RoutedEventArgs e)
    {
        _working.OffsetX = 0;
        _working.OffsetY = 0;
        _working.Width = 460;
        _working.Height = 0;
        _working.VerticalAlign = 0;

        // Push through the sliders so the UI stays in sync with the model.
        _loading = true;
        try
        {
            OffsetXSlider.Value = 0;
            OffsetYSlider.Value = 0;
            WidthSlider.Value = 460;
            HeightSlider.Value = 0;
            VerticalAlignSlider.Value = 0;
        }
        finally
        {
            _loading = false;
        }

        UpdateValueLabels();
        _applyLive(_working);
        SetStatus("已恢复默认位置、宽度与高度");
    }

    // ---- footer actions ------------------------------------------------

    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "将把所有外观、位置与来源设置恢复为默认值（歌词来源缓存不受影响）。\n\n继续吗？",
            "恢复默认设置", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        // Keep only fields that are not user-facing preferences.
        var defaults = new AppSettings
        {
            Positions = _working.Positions,
            StartWithWindows = _working.StartWithWindows,
        };

        _working = defaults;
        LoadFromWorking();
        _applyLive(_working);
        SetStatus("已恢复默认设置（点击「保存」写入，或「取消」放弃）");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ReadIntoWorking();

        // Preserve non-UI state that the dialog does not edit.
        _working.Positions = _original.Positions;
        _working.StartWithWindows = _original.StartWithWindows;

        _save(_working);
        SetStatus("已保存，可继续调整或关闭窗口");
    }

    /// <summary>
    /// Close the window but keep the app and the taskbar lyrics running.
    /// <para>
    /// Edits are kept as well — every change already applied live, so silently
    /// reverting them here would be surprising. Reopening the window restores the
    /// applied state, and "恢复默认设置" is the deliberate way to undo everything.
    /// </para>
    /// </summary>
    private void OnCloseWindow(object sender, RoutedEventArgs e)
    {
        ReadIntoWorking();
        _working.Positions = _original.Positions;
        _working.StartWithWindows = _original.StartWithWindows;

        // Persist so the window can be closed without losing adjustments.
        _save(_working);
        Close();
    }

    private void OnQuit(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "将关闭任务栏歌词并退出程序。\n\n确定要退出吗？",
            "退出程序", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        // Persist current edits first so nothing is lost on the way out.
        ReadIntoWorking();
        _working.Positions = _original.Positions;
        _working.StartWithWindows = _original.StartWithWindows;
        _save(_working);

        _quit?.Invoke();
    }

    private void OnRematch(object sender, RoutedEventArgs e)
    {
        _rematch?.Invoke();
        SetStatus("已请求重新匹配当前歌曲");
    }

    private void OnClearCache(object sender, RoutedEventArgs e)
    {
        _clearCache?.Invoke();
        SetStatus("已清空歌词缓存并重新匹配");
    }

    private void OnShowDiagnostics(object sender, RoutedEventArgs e) => _showDiagnostics?.Invoke();

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}
