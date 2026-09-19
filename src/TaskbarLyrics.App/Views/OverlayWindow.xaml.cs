using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TaskbarLyrics.App.Configuration;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Interop;
using TaskbarLyrics.App.Platform;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.App.Views;

/// <summary>
/// The always-on-top, borderless window that draws lyrics directly over the
/// taskbar's empty region.
/// <para>
/// The window deliberately never activates (<c>WS_EX_NOACTIVATE</c>), so
/// clicking it cannot steal focus from the media player. While "locked" it is
/// also click-through, which means the taskbar under it stays completely usable.
/// </para>
/// </summary>
public partial class OverlayWindow : Window
{
    /// <summary>Narrowest the overlay may become before it is simply unreadable.</summary>
    private const double MinOverlayWidth = 160;
    private const double EdgePadding = 8;

    /// <summary>Gap in DIP between the taskbar and the strip when floating above it.</summary>
    private const double AboveTaskbarGap = 2;

    /// <summary>
    /// How often the Z-order guard may run, in milliseconds.
    /// <para>
    /// Deliberately short. The taskbar is topmost too, and Explorer re-raises it
    /// roughly once a second; while it is above us it paints over the strip. A long
    /// interval left the lyrics covered for up to a quarter second, which reads as a
    /// visible flicker. Recovering within ~2 frames makes it imperceptible.
    /// </para>
    /// </summary>
    private const long TopmostCheckIntervalMs = 30;

    private long _lastTopmostCheckMs;

    /// <summary>
    /// True while the overlay is parented to the taskbar. In that state Z-order is
    /// the taskbar's concern, so the re-assert guard is unnecessary.
    /// </summary>
    private bool _ownedByTaskbar;

    /// <summary>Off-UI-thread Z-order guard (see TaskbarOcclusionGuard).</summary>
    private TaskbarOcclusionGuard? _occlusionGuard;



    /// <summary>Event-driven trigger for Z-order recovery (see TaskbarZOrderWatcher).</summary>
    private TaskbarZOrderWatcher? _zOrderWatcher;

    private AppSettings _settings;
    private readonly CompositingMode _compositing;
    private TaskbarInfo? _taskbar;
    private bool _dragging;
    private Point _dragOrigin;
    private double _dragStartOffsetX;
    private bool _positioned;

    private readonly DispatcherTimer _repositionTimer;

    public OverlayWindow(AppSettings settings, CompositingMode compositing)
    {
        _settings = settings;
        _compositing = compositing;

        // AllowsTransparency must be decided before the window handle exists, so
        // it is driven by the caller-selected compositing mode.
        if (compositing == CompositingMode.ColorKey)
        {
            AllowsTransparency = false;
            WindowStyle = WindowStyle.None;
            Background = new SolidColorBrush(TransparencyMode.KeyColor);
        }

        InitializeComponent();

        // The overlay is chromeless, but the icon still matters for the Alt+Tab
        // list and window enumeration.
        AppIcons.ApplyTo(this);

        // Follow taskbar/DPI changes (resolution changes, monitor hot-plug, bar
        // auto-hide toggling) without needing the user to restart.
        _repositionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _repositionTimer.Tick += (_, _) => RepositionIfChanged();
        _repositionTimer.Start();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => ApplySettingsToVisuals();
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;

        // Release the Win32 hook and the guard thread with the window, so neither
        // keeps running (or keeps a delegate alive) after the overlay is gone.
        Closed += (_, _) =>
        {
            _occlusionGuard?.Dispose();
            _occlusionGuard = null;

            _zOrderWatcher?.Dispose();
            _zOrderWatcher = null;
        };
    }

    /// <summary>Raised when a transport button is pressed.</summary>
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;

    /// <summary>Exposes the active compositing mode for diagnostics.</summary>
    public CompositingMode Compositing => _compositing;

    /// <summary>Raised when the user finishes dragging, so settings can be saved.</summary>
    public event EventHandler? PositionChanged;

    /// <summary>Raised when the user clicks the overlay while it is unlocked.</summary>
    public event EventHandler? OverlayClicked;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Diag.Log($"[overlay] SourceInitialized mode={_compositing} " +
                 $"handle={new WindowInteropHelper(this).Handle}");

        ApplyOverlayStyles();

        // Hit-testing hook: decides per element whether a click belongs to us or
        // should fall through to the taskbar.
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        source?.AddHook(WndProc);


        _taskbar = TaskbarLocator.Locate(this);
        Diag.Log($"[overlay] taskbar edge={_taskbar?.Edge} " +
                 $"bounds=({_taskbar?.Bounds.Left},{_taskbar?.Bounds.Top})-" +
                 $"({_taskbar?.Bounds.Right},{_taskbar?.Bounds.Bottom}) scale={_taskbar?.Scale}");

        TrySetTaskbarAsOwner();
        PlaceOverWindow();

        // Needs the window placed first: it samples the taskbar colour beside the strip.
        UpdateTransportContrast();

        // Re-assert from a dedicated thread. Marshalling through the dispatcher put the
        // recovery behind whatever WPF was rendering, measured as the lyrics staying
        // covered for ~150 ms after the taskbar came forward.
        _occlusionGuard = new TaskbarOcclusionGuard(new WindowInteropHelper(this).Handle);
        _occlusionGuard.Start();

        // Event-driven Z-order recovery: the guard handles the common case, and this
        // covers the taskbar raising itself without becoming our immediate neighbour.
        _zOrderWatcher = new TaskbarZOrderWatcher();
        _zOrderWatcher.TaskbarRaised += OnTaskbarRaised;
        _zOrderWatcher.Start();
    }

    /// <summary>
    /// Hook callback. Arrives on the hook thread, so it is marshalled to the UI
    /// thread, where the window handle and layout live.
    /// </summary>
    private void OnTaskbarRaised()
    {
        try
        {
            // First, and on this thread: waking the guard is a single signal, so the
            // re-assert happens immediately instead of queuing behind WPF's rendering.
            _occlusionGuard?.Notify();

            if (Dispatcher.HasShutdownStarted) return;

            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                // Bypass the throttle: this is exactly the moment the taskbar has
                // just come forward.
                _lastTopmostCheckMs = Environment.TickCount64;
                EnsureAboveTaskbar();

                // The taskbar's raise is not a single instant — activation, the
                // auto-hide animation and the repaint settle over the following tens
                // of milliseconds. Re-asserting only immediately can lose that race
                // and leave us covered, so re-check after it has settled.
                ScheduleReassert(40);
                ScheduleReassert(140);
            }));
        }
        catch
        {
            // Never let a hook callback throw.
        }
    }

    /// <summary>Re-assert the Z-order once, after a short delay.</summary>
    private void ScheduleReassert(int delayMs)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Send, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(delayMs),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            if (Dispatcher.HasShutdownStarted) return;

            _lastTopmostCheckMs = Environment.TickCount64;
            EnsureAboveTaskbar();
        };

        timer.Start();
    }

    /// <summary>
    /// Make the taskbar this window's owner so Windows itself keeps us above it.
    /// <para>
    /// An owned window is always above its owner, so this removes the Z-order race
    /// instead of trying to win it. Re-applied on the reposition timer because
    /// Explorer can restart and recreate <c>Shell_TrayWnd</c>, and because WPF can
    /// reset the owner while it finishes setting the window up.
    /// </para>
    /// </summary>
    private void TrySetTaskbarAsOwner()
    {
        // Escape hatch for A/B measurement: lets the same binary be run with and
        // without the owner relationship so its effect can be attributed rather than
        // assumed.
        if (Environment.GetEnvironmentVariable("TBL_NO_OWNER") == "1")
        {
            _ownedByTaskbar = false;
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            Diag.Log("[overlay] owner: window handle not ready");
            return;
        }

        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            Diag.Log("[overlay] owner: Shell_TrayWnd not found");
            return;
        }

        bool ok = NativeMethods.SetOwner(handle, taskbar, out var detail);

        // Log failures every time, and successes only on change, so the log shows
        // both whether it ever took and whether something reverts it.
        if (!ok || ok != _ownedByTaskbar)
        {
            Diag.Log($"[overlay] owner window=0x{handle.ToInt64():X} " +
                     $"taskbar=0x{taskbar.ToInt64():X} ok={ok} ({detail})");
        }

        _ownedByTaskbar = ok;
    }

    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTTRANSPARENT = -1;

    /// <summary>
    /// Answer hit tests per element.
    /// <para>
    /// When interactive, the entire overlay area is hit-testable so the user can
    /// drag from anywhere on the strip and clicks do not leak through to the
    /// taskbar (which would re-assert its Z-order and cause a visible flash).
    /// When locked (click-through), only the transport buttons remain clickable;
    /// everything else returns <c>HTTRANSPARENT</c> so the taskbar stays usable.
    /// </para>
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {

        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        if (!_settings.Interactive)
        {
            // Locked mode: only transport buttons are clickable, everything else
            // falls through to the taskbar.
            if (_settings.ShowTransportControls)
            {
                int packed = lParam.ToInt32();
                var screenX = (short)(packed & 0xFFFF);
                var screenY = (short)((packed >> 16) & 0xFFFF);

                Point local;
                try
                {
                    local = PointFromScreen(new Point(screenX, screenY));
                }
                catch
                {
                    return IntPtr.Zero;
                }

                if (IsOverAButton(local))
                {
                    handled = true;
                    return new IntPtr(HTCLIENT);
                }
            }

            handled = true;
            return new IntPtr(HTTRANSPARENT);
        }

        // Interactive mode: the entire overlay captures input so the user can drag
        // from anywhere, and clicks never reach the taskbar (preventing Z-order
        // fights that cause flicker).
        handled = true;
        return new IntPtr(HTCLIENT);
    }

    /// <summary>True when the point lies on a transport button.</summary>
    private bool IsOverInteractiveElement(Point local)
    {
        return _settings.ShowTransportControls && IsOverAButton(local);
    }

    private bool IsOverAButton(Point local)
    {
        foreach (var button in new[] { PrevButton, PlayPauseButton, NextButton })
        {
            try
            {
                var origin = button.TransformToAncestor(this).Transform(new Point(0, 0));
                if (new Rect(origin, new Size(button.ActualWidth, button.ActualHeight)).Contains(local))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore.
            }
        }

        return false;
    }

    private TimeSpan _lastProgress;
    private TimeSpan _lastDuration;

    /// <summary>
    /// Update the song progress bar and time readout shown under the transport
    /// buttons.
    /// </summary>
    public void SetProgress(TimeSpan position, TimeSpan duration)
    {
        _lastProgress = position;
        _lastDuration = duration;

        if (duration <= TimeSpan.Zero)
        {
            // Nothing meaningful to show until the player reports a duration.
            ProgressText.Text = "--:-- / --:--";
            ProgressFill.Width = 0;
            return;
        }

        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > duration) position = duration;

        ProgressText.Text = $"{Format(position)} / {Format(duration)}";

        // Width comes from the track's measured width, so it stays correct across
        // resizes and DPI changes with no binding machinery.
        var trackWidth = ProgressTrack.ActualWidth;
        ProgressFill.Width = trackWidth > 0
            ? Math.Clamp(trackWidth * (position.TotalSeconds / duration.TotalSeconds), 0, trackWidth)
            : 0;
    }

    /// <summary>m:ss for tracks under an hour, h:mm:ss above it.</summary>
    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>
    /// Recompute the fill width when the track resizes. ActualWidth is only valid
    /// after layout, so the last known progress is replayed.
    /// </summary>
    private void OnProgressTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SetProgress(_lastProgress, _lastDuration);
    }

    /// <summary>
    /// Keep the transport glyphs and progress readout legible whatever is behind them.
    /// <para>
    /// With the readability plate disabled the strip is drawn straight onto the taskbar,
    /// where the fixed near-white glyphs vanish against a light bar. The taskbar's own
    /// colour is sampled just outside the strip — those points are not covered by this
    /// window, so they report the real backdrop — and the glyphs flip to dark or light
    /// accordingly, always with an opposite-coloured halo so they also survive a
    /// gradient or a wallpaper edge.
    /// </para>
    /// </summary>
    private void UpdateTransportContrast()
    {
        // With the plate on, the backdrop is our own dark fill, so the light glyphs are
        // already right and sampling the taskbar would give the wrong answer.
        if (_settings.ShowBackground)
        {
            ApplyTransportColors(Colors.White, Colors.Black);
            return;
        }

        double? luminance = SampleBackdropLuminance();

        if (luminance is > 0.55)
        {
            ApplyTransportColors(Color.FromRgb(0x14, 0x14, 0x14), Colors.White);
        }
        else
        {
            // Dark backdrop, or no reading at all: light glyphs, which the halo keeps
            // readable either way.
            ApplyTransportColors(Colors.White, Colors.Black);
        }
    }

    private void ApplyTransportColors(Color glyph, Color halo)
    {
        var brush = new SolidColorBrush(glyph);
        brush.Freeze();

        foreach (var button in new[] { PrevButton, PlayPauseButton, NextButton })
        {
            button.Foreground = brush;

            // ShadowDepth 0 with a small blur is a halo rather than a shadow: it
            // outlines the glyph instead of offsetting it.
            button.Effect = new DropShadowEffect
            {
                Color = halo,
                ShadowDepth = 0,
                BlurRadius = 3,
                Opacity = 0.9,
            };
        }

        var textBrush = new SolidColorBrush(Color.FromArgb(0xE6, glyph.R, glyph.G, glyph.B));
        textBrush.Freeze();
        ProgressText.Foreground = textBrush;
        ProgressText.Effect = new DropShadowEffect
        {
            Color = halo,
            ShadowDepth = 0,
            BlurRadius = 3,
            Opacity = 0.9,
        };
    }

    /// <summary>
    /// Average brightness (0..1) of the taskbar immediately left and right of the strip,
    /// or null when neither side could be read.
    /// </summary>
    private double? SampleBackdropLuminance()
    {
        if (_taskbar is null || !_positioned) return null;

        var scale = _taskbar.Scale <= 0 ? 1.0 : _taskbar.Scale;

        // Physical pixels: the screen DC knows nothing of WPF's DIP coordinates.
        int left = (int)Math.Round(Left * scale) - 24;
        int right = (int)Math.Round((Left + ActualWidth) * scale) + 24;
        int y = (int)Math.Round((Top + (ActualHeight / 2)) * scale);

        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;

        try
        {
            double total = 0;
            int samples = 0;

            foreach (var x in new[] { left, right })
            {
                // Only sample points that are genuinely on the taskbar band.
                if (x < _taskbar.Bounds.Left + 2 || x > _taskbar.Bounds.Right - 2) continue;
                if (y < _taskbar.Bounds.Top || y > _taskbar.Bounds.Bottom) continue;

                uint pixel = GetPixel(hdc, x, y);
                if (pixel == ClrInvalid) continue;

                int r = (int)(pixel & 0xFF);
                int g = (int)((pixel >> 8) & 0xFF);
                int b = (int)((pixel >> 16) & 0xFF);

                total += ((0.2126 * r) + (0.7152 * g) + (0.0722 * b)) / 255.0;
                samples++;
            }

            return samples == 0 ? null : total / samples;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private const uint ClrInvalid = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e) =>
        PlayPauseRequested?.Invoke(this, EventArgs.Empty);

    private void OnNextClicked(object sender, RoutedEventArgs e) =>
        NextRequested?.Invoke(this, EventArgs.Empty);

    private void OnPrevClicked(object sender, RoutedEventArgs e) =>
        PreviousRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Verify that this window's chosen transparency actually reaches the screen.
    /// Only meaningful for the per-pixel-alpha mode.
    /// <para>
    /// The visual tree is hidden during the check and the window background is set
    /// to an opaque sentinel colour. Without that, the content's own translucent
    /// backdrop is what gets sampled, and the probe cannot tell "we composited"
    /// apart from "we are invisible".
    /// </para>
    /// </summary>
    public bool VerifyCompositing()
    {
        if (_compositing != CompositingMode.PerPixelAlpha) return true;

        // No sentinel colour is drawn: the check queries the compositor instead of
        // painting the screen, so startup no longer flashes.
        return TransparencyMode.VerifyPerPixelAlpha(this);
    }

    /// <summary>
    /// Re-assert position above the taskbar, but only when it has actually climbed
    /// above us. Uses <see cref="NativeMethods.SetAbove"/> to insert directly above
    /// the taskbar rather than jumping to the absolute top, which is gentler on the
    /// Z-order and produces less visible flicker.
    /// </summary>
    private void EnsureAboveTaskbar()
    {
        if (!IsVisible) return;



        try
        {
            var mine = new WindowInteropHelper(this).Handle;
            if (mine == IntPtr.Zero) return;

            var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return;

            if (!NativeMethods.IsTopmost(taskbar)) return;

            if (!NativeMethods.IsAbove(taskbar, mine))
            {
                return;
            }

            Diag.Log("[overlay] taskbar is above us; re-asserting Z-order");

            // Move straight to the top of the topmost band. SetAbove would insert us
            // just above the taskbar, which forces the taskbar down as a second
            // Z-order change; a single HWND_TOPMOST move is one operation and cannot
            // leave the taskbar "just above" us.
            NativeMethods.SetTopmost(this);
        }
        catch (Exception ex)
        {
            Diag.Log($"[overlay] EnsureAboveTaskbar failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-read the taskbar rectangle and reposition only when it actually moved,
    /// so the 2-second timer is cheap.
    /// </summary>
    private void RepositionIfChanged()
    {
        if (!_positioned) return;

        // Explorer may have restarted and recreated the taskbar, which would leave
        // us parented to a dead window; re-assert the embedding.
        TrySetTaskbarAsOwner();

        // Slow re-evaluation so a wallpaper or taskbar colour change is picked up.
        UpdateTransportContrast();

        // The taskbar re-asserts itself as topmost whenever it is clicked or
        // activated, which silently pushes this window down inside the topmost
        // band. Without re-asserting, the window stays "visible" but the taskbar
        // paints over it — the lyrics appear to vanish while the window is fine.
        // Embedding removes the need entirely.
        EnsureAboveTaskbar();

        var latest = TaskbarLocator.Locate(this);
        if (latest is null) return;

        if (_taskbar is null ||
            latest.Bounds.Left != _taskbar.Bounds.Left ||
            latest.Bounds.Top != _taskbar.Bounds.Top ||
            latest.Bounds.Width != _taskbar.Bounds.Width ||
            latest.Bounds.Height != _taskbar.Bounds.Height ||
            Math.Abs(latest.Scale - _taskbar.Scale) > 0.001)
        {
            _taskbar = latest;
            PlaceOverWindow();
        }
    }

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        ApplySettingsToVisuals();
        PlaceOverWindow();
    }

    private void ApplySettingsToVisuals()
    {
        ApplyOverlayStyles();

        Diag.Log($"[overlay] ApplySettingsToVisuals showBg={_settings.ShowBackground} " +
                 $"bg={_settings.BackgroundColor} base={_settings.BaseColor} " +
                 $"hl={_settings.HighlightColor} ctx={_settings.ContextColor} " +
                 $"font={_settings.FontFamily}/{_settings.OriginalFontSize} " +
                 $"showTrans={_settings.ShowTranslation} locked={_settings.Locked}");

        foreach (var line in new[] { CurrentLine, NextLine })
        {
            line.FontFamilyName = _settings.FontFamily;
            line.FontSizeValue = _settings.OriginalFontSize;
            line.TranslationFontSizeValue = _settings.TranslationFontSize;
            line.FontWeightName = _settings.FontWeight;
            line.LetterSpacing = _settings.LetterSpacing;
            line.HighlightColor = ParseBrush(_settings.HighlightColor, Brushes.DeepSkyBlue);
            line.BaseColor = ParseBrush(_settings.BaseColor, Brushes.WhiteSmoke);
            line.ContextColor = ParseBrush(_settings.ContextColor, Brushes.LightGray);
            line.WordHighlightEnabled = _settings.EnableWordHighlight;
        }

        // Transport buttons are optional; hidden buttons must not capture clicks,
        // otherwise the strip would swallow taskbar input for no visible reason.
        TransportPanel.Visibility = _settings.ShowTransportControls
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Progress sits under the buttons and is independently toggleable.
        ProgressSection.Visibility = _settings.ShowSongProgress
            ? Visibility.Visible
            : Visibility.Collapsed;

        ProgressFill.Background = ParseBrush(_settings.ProgressBarColor,
            new SolidColorBrush(Color.FromRgb(0x3A, 0xBE, 0xFF)));

        // Whole-window opacity: fades text and panel together, and clamps away
        // values that would make the overlay effectively invisible.
        Opacity = Math.Clamp(_settings.OverlayOpacity, 0.15, 1.0);

        if (_compositing == CompositingMode.ColorKey)
        {
            // Per-pixel alpha is unavailable, so WPF cannot draw a translucent
            // backdrop: the window background shows through instead. Hide the
            // Backdrop element (it would paint an opaque rectangle) and paint the
            // panel colour onto the window itself, keeping a margin around the
            // region so the rounded window shape frames the text.
            Backdrop.Background = Brushes.Transparent;
            Background = _settings.ShowBackground
                ? new SolidColorBrush(OpaquePanelColor(_settings.BackgroundColor))
                : new SolidColorBrush(TransparencyMode.KeyColor);
        }
        else
        {
            Backdrop.Background = _settings.ShowBackground
                ? ParseBrush(_settings.BackgroundColor, new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)))
                : Brushes.Transparent;
        }

        Backdrop.CornerRadius = new CornerRadius(_settings.BackgroundCornerRadius);

        NextLine.Visibility = _settings.ShowContextLines ? Visibility.Visible : Visibility.Collapsed;

        Width = Math.Max(MinOverlayWidth, _settings.Width);
    }

    /// <summary>
    /// Convert a possibly-translucent panel colour into an opaque one, since the
    /// colour-key path cannot blend. Alpha becomes a simple darkening against the
    /// assumed taskbar background.
    /// </summary>
    private static Color OpaquePanelColor(string? configured)
    {
        var baseColor = Color.FromRgb(0x1E, 0x1E, 0x1E);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                baseColor = (Color)ColorConverter.ConvertFromString(configured);
            }
            catch
            {
                // Keep the default.
            }
        }

        // Already opaque: use as-is.
        if (baseColor.A == 255) return baseColor;

        // Otherwise darken towards black in proportion to the intended alpha, so
        // "66 alpha black" still reads as a dark panel.
        var factor = baseColor.A / 255.0;
        return Color.FromRgb(
            (byte)(baseColor.R * factor),
            (byte)(baseColor.G * factor),
            (byte)(baseColor.B * factor));
    }

    private static string Describe(Brush? brush) => brush switch
    {
        null => "null",
        SolidColorBrush s => s.Color.ToString(),
        _ => brush.GetType().Name,
    };

    /// <summary>True when this window draws its transparency per-pixel.</summary>
    public bool UsesPerPixelAlpha => _compositing == CompositingMode.PerPixelAlpha;

    private static Brush ParseBrush(string? value, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            return brush;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Apply the no-activate extended window styles.</summary>
    private void ApplyOverlayStyles()
    {
        // clickThrough is deliberately false now: WS_EX_TRANSPARENT makes the OS
        // skip this window during hit testing altogether, so our WM_NCHITTEST
        // handler would never run and nothing could be dragged or clicked.
        // Pass-through is decided per element in WndProc instead.
        NativeMethods.ApplyOverlayStyles(this, clickThrough: false);
    }

    /// <summary>
    /// Position the overlay over the taskbar, immediately left of the
    /// notification area.
    /// <para>
    /// All Win32 values arrive in device pixels and are converted to DIP by
    /// dividing by <see cref="TaskbarInfo.Scale"/>, because that is the factor
    /// WPF applies to <c>Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c>. Getting
    /// this wrong shifts and stretches the overlay on any scaled display.
    /// </para>
    /// <para>
    /// If the requested width would push the window past the free space between
    /// the start button and the tray (or off the screen), the width is reduced
    /// first and the position clamped second. That guarantees lyrics never cover
    /// taskbar buttons, whatever the icon count.
    /// </para>
    /// </summary>
    public void PlaceOverWindow()
    {
        // Pass "this" so the scale matches WPF's own interpretation exactly.
        _taskbar = TaskbarLocator.Locate(this);
        if (_taskbar is null) return;

        var scale = _taskbar.Scale;

        // Overlay occupies the taskbar band, inset slightly so it never spills
        // over the screen edge. A user-specified height wins, but is clamped to
        // the band so the strip cannot spill outside the taskbar.
        double fittedHeight = Math.Max(16, _taskbar.HeightDip - 2);
        double requestedHeight = _settings.Height > 0 ? _settings.Height : fittedHeight;
        Height = Math.Clamp(requestedHeight, 16, _taskbar.HeightDip);

        var width = Math.Max(MinOverlayWidth, _settings.Width);

        // The right edge of the overlay sits at the left edge of the tray plus
        // the user's horizontal offset.
        var trayLeftDevice = TaskbarLocator.GetTrayLeftDevicePixels();
        double anchorRightDevice = double.IsNaN(trayLeftDevice)
            ? _taskbar.Bounds.Right          // no tray found: use the taskbar's right edge
            : trayLeftDevice;

        double anchorRight = anchorRightDevice / scale;
        double right = anchorRight + _settings.OffsetX;
        double left = right - width;

        // Keep clear of the taskbar's left content (start button / search).
        double freeLeft = _taskbar.LeftDip + EdgePadding;
        if (left < freeLeft)
        {
            // Prefer shrinking to keep the right edge anchored to the tray.
            var available = right - freeLeft;
            if (available >= MinOverlayWidth)
            {
                width = available;
                left = freeLeft;
            }
            else
            {
                // Not enough room to the left; clamp position instead.
                width = MinOverlayWidth;
                left = Math.Max(freeLeft, right - width);
            }
        }

        // Never extend past the monitor's right edge.
        double maxRight = _taskbar.MonitorRightDip - EdgePadding;
        if (left + width > maxRight)
        {
            left = Math.Max(freeLeft, maxRight - width);
        }

        // Vertical placement: centre in the band, then apply the optical nudge and
        // the free-form offset. Clamped so the strip always stays on the taskbar.
        double bandTop = _taskbar.TopDip;
        double bandBottom = bandTop + _taskbar.HeightDip;
        double centred = bandTop + ((_taskbar.HeightDip - Height) / 2.0);

        // VerticalAlign is a fraction of the band height (−1 top … +1 bottom).
        double alignNudge = _settings.VerticalAlign * (_taskbar.HeightDip - Height) / 2.0;

        var top = centred + alignNudge + _settings.OffsetY;
        top = Math.Clamp(top, bandTop, Math.Max(bandTop, bandBottom - Height));

        if (_settings.PlaceAboveTaskbar)
        {
            // Float clear of the bar. This is the only way to guarantee no flicker:
            // with no overlap, the taskbar can raise itself as often as it likes.
            top = bandTop - Height - AboveTaskbarGap;
        }


        Left = left;
        Top = top;
        Width = width;

        _positioned = true;

        // The colour-key path needs its region recomputed after every resize,
        // and its sentinel background must survive the settings refresh.
        if (_compositing == CompositingMode.ColorKey)
        {
            UpdateLayout();
            TransparencyMode.ApplyColorKey(this, _settings.BackgroundCornerRadius);
        }


        // Insert above the taskbar gently rather than jumping to HWND_TOPMOST,
        // which would fight the taskbar's own Z-order re-assertions.
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero)
        {
            NativeMethods.SetAbove(this, taskbar);
        }
        else
        {
            NativeMethods.SetTopmost(this);
        }
    }

    /// <summary>
    /// Send the lyric state to the visuals. Called on every UI tick.
    /// </summary>
    public void Render(LyricDocument? document, TimeSpan position, bool isPlaying)
    {
        // Z-order guard. Runs regardless of ownership: the owner relationship is
        // best-effort (Windows clears it again after a few seconds), so it must never
        // be what stands between us and being repainted.
        if (
            Environment.TickCount64 - _lastTopmostCheckMs >= TopmostCheckIntervalMs)
        {
            _lastTopmostCheckMs = Environment.TickCount64;
            EnsureAboveTaskbar();
        }

        try
        {
            RenderCore(document, position, isPlaying);
        }
        catch (Exception ex)
        {
            Diag.Log($"[overlay] Render EXCEPTION {ex}");
        }
    }

    private void RenderCore(LyricDocument? document, TimeSpan position, bool isPlaying)
    {
        if (document is null || document.IsEmpty)
        {
            CurrentLine.Text = string.Empty;
            CurrentLine.Translation = null;
            NextLine.Text = string.Empty;
            NextLine.Translation = null;
            Diag.Log("[overlay] Render -> empty document");
            return;
        }

        var index = document.IndexAt(position);
        Diag.Log($"[overlay] Render pos={position:mm\\:ss\\.ff} index={index} " +
                 $"lines={document.Lines.Count} W={Width} H={Height} " +
                 $"ActualW={ActualWidth:F1} ActualH={ActualHeight:F1} " +
                 $"vis={IsVisible} left={Left:F0} top={Top:F0}");
        if (index < 0)
        {
            // Pre-roll: show the first upcoming line without a sweep.
            CurrentLine.Text = document.Lines.Count > 0 ? document.Lines[0].Text : string.Empty;
            CurrentLine.Translation = _settings.ShowTranslation
                ? document.Lines.FirstOrDefault()?.Translation
                : null;
            CurrentLine.Syllables = null;
            CurrentLine.Progress = 0;
            SetNext(document, 1);
            return;
        }

        var line = document.Lines[index];

        CurrentLine.Text = line.Text;
        CurrentLine.Translation = _settings.ShowTranslation ? line.Translation : null;
        CurrentLine.Syllables = line.Syllables.Count > 0 ? line.Syllables : null;
        CurrentLine.LineStart = line.Start;
        // Use the *sung* span, not the gap to the next line: that gap contains any
        // instrumental passage, and sweeping the highlight across it is what made the
        // progress disagree with the actual singing.
        CurrentLine.LineEnd = line.Start + line.SungDuration;
        CurrentLine.Progress = ComputeProgress(line, position);

        SetNext(document, index + 1);
    }

    private void SetNext(LyricDocument document, int index)
    {
        if (!_settings.ShowContextLines || index < 0 || index >= document.Lines.Count)
        {
            NextLine.Text = string.Empty;
            NextLine.Translation = null;
            return;
        }

        var next = document.Lines[index];
        NextLine.Text = next.Text;
        NextLine.Translation = _settings.ShowTranslation ? next.Translation : null;
        NextLine.Progress = 0;
    }

    private static double ComputeProgress(LyricLine line, TimeSpan position)
    {
        // Progress runs over the sung span. Once it completes the line holds fully
        // highlighted while any instrumental tail plays out — the correct behaviour,
        // and what stops the sweep from lagging behind the voice.
        var span = line.SungDuration > TimeSpan.Zero ? line.SungDuration : line.Duration;
        if (span <= TimeSpan.Zero) return 1;

        var elapsed = position - line.Start;
        if (elapsed <= TimeSpan.Zero) return 0;
        if (elapsed >= span) return 1;

        return elapsed / span;
    }

    // ---- dragging ------------------------------------------------------

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_settings.Interactive)
        {
            return;
        }

        _dragging = true;
        _dragOrigin = PointToScreen(e.GetPosition(this));
        _dragStartOffsetX = _settings.OffsetX;

        // Pause the reposition timer during drag so it does not fight the live
        // preview by calling PlaceOverWindow with stale offsets.
        _repositionTimer.Stop();

        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var current = PointToScreen(e.GetPosition(this));
        var scale = _taskbar?.Scale ?? 1.0;

        // Horizontal only. The strip is anchored inside the taskbar band, so letting
        // the pointer drag it vertically would push lyrics off the taskbar; vertical
        // placement stays under "垂直对齐"/"垂直偏移" in settings instead.
        var dxDip = (current.X - _dragOrigin.X) / scale;

        _settings.OffsetX = _dragStartOffsetX + dxDip;

        PlaceOverWindow();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();

        // Resume the reposition timer now that the drag is finished.
        _repositionTimer.Start();

        PlaceOverWindow();
        PositionChanged?.Invoke(this, EventArgs.Empty);
        OverlayClicked?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
