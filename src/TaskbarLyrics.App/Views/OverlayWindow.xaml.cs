using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    /// How close (DIP) a dragged edge has to come to a snap line before it sticks to
    /// it. Small enough to leave free placement alone, large enough to hit the edge
    /// without slowing the drag down.
    /// </summary>
    private const double SnapDistance = 12;

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
    /// A single step of alpha painted across the strip (1/255 of black).
    /// <para>
    /// Invisible to the eye, but not to Windows: a layered window is hit-tested by its
    /// own alpha, so pixels at alpha 0 are transparent to the mouse as well as the eye.
    /// Without this floor an empty strip never receives a hover, a drag, or a click on
    /// a button whose own chrome is transparent until it is hovered.
    /// </para>
    /// </summary>
    private static readonly Brush HitTestFloor = Frozen(Color.FromArgb(1, 0, 0, 0));

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

    /// <summary>Strip's own top-left corner (DIP) when the drag started.</summary>
    private double _dragStartLeft;
    private double _dragStartTop;

    /// <summary>True once the pointer has moved far enough to count as a drag, not a click.</summary>
    private bool _dragMoved;

    /// <summary>Last position written to the drag diagnostic, so it logs movement only.</summary>
    private int _diagDragX = int.MinValue;
    private int _diagDragY = int.MinValue;

    /// <summary>True once a free position has been applied, so it is only seeded once.</summary>
    private bool _freeSeeded;

    private bool _positioned;

    /// <summary>True when the transport controls stay hidden until the strip is hovered.</summary>
    private bool _hoverReveal;

    /// <summary>True while the pointer is anywhere over the strip.</summary>
    private bool _hovered;

    /// <summary>
    /// Colour the transport glyphs were last tinted with, so the drag pill can be
    /// brightened for the duration of a drag without re-reading the taskbar.
    /// </summary>
    private Color _transportInk = Colors.White;

    /// <summary>
    /// The two play/pause glyphs, resolved once. Swapping between two resources keeps the
    /// button's own layout untouched — the two are authored in the same view box, so the
    /// swap cannot shift or resize what is already on screen.
    /// </summary>
    private readonly Geometry _playIcon;
    private readonly Geometry _pauseIcon;

    /// <summary>
    /// Lyric text currently on screen. Kept so a line change — and only a line
    /// change — can be faded in; the render tick rewrites the same text ~12 times a
    /// second and must not restart the animation each time.
    /// </summary>
    private string _renderedText = string.Empty;

    /// <summary>Last line index and width written to the diag log, so it is logged on change.</summary>
    private int _diagIndex = int.MinValue;
    private double _diagWidth = double.NaN;

    /// <summary>Last lyric inset written to the diag log; see <see cref="UpdateLyricShift"/>.</summary>
    private double _diagInset = double.NaN;

    /// <summary>
    /// Album art for the hover cluster, and the brush that paints it. The brush is kept as
    /// a field rather than rebuilt per track, so a song change costs one image decode and
    /// nothing else.
    /// </summary>
    private ImageSource? _coverArt;

    private readonly ImageBrush _coverBrush = new()
    {
        Stretch = Stretch.UniformToFill,
        AlignmentX = AlignmentX.Center,
        AlignmentY = AlignmentY.Center,
    };

    private readonly DispatcherTimer _repositionTimer;

    /// <summary>Polls the pointer for the hover reveal; runs only while it is enabled.</summary>
    private readonly DispatcherTimer _pointerTimer;

    /// <summary>How often the pointer is polled. Well under the fade duration.</summary>
    private const int PointerPollMs = 100;

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

        _playIcon = (Geometry)FindResource("IconPlay");
        _pauseIcon = (Geometry)FindResource("IconPause");

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

        // Hover reveal for the transport controls, polled rather than driven by
        // MouseEnter/MouseLeave. Re-asserting our own Z-order under a stationary
        // pointer makes Windows cancel mouse tracking, so WPF reports a MouseLeave
        // that never happened and then sends nothing until the pointer physically
        // moves again — measured as the controls appearing and disappearing ~170 ms
        // later, with no way to bring them back. The cursor position is authoritative
        // and costs one GetCursorPos per tick.
        _pointerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PointerPollMs),
        };
        _pointerTimer.Tick += (_, _) => PollPointer();

        // The cluster decides how far the words have to travel, and its width is only known
        // once it has been measured — then again whenever the title, the artist, the cover or
        // the readout changes it. A size change is the one signal all of those share.
        LeftCluster.SizeChanged += (_, _) => UpdateLyricShift(animate: false);

        // The clip that keeps the words out from under the cluster is built from the lines'
        // own width, so it has to be rebuilt whenever the strip is resized.
        CurrentLine.SizeChanged += (_, _) => UpdateLyricShift(animate: false);
        NextLine.SizeChanged += (_, _) => UpdateLyricShift(animate: false);

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
        RefreshBackdropContrast();

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
    /// State last pushed to the progress panel. SetProgress runs on every render tick, so
    /// it compares against these instead of rebuilding the readout and re-assigning the
    /// fill width (which invalidates layout for the strip) 60 times a second.
    /// </summary>
    private int _shownSeconds = -1;
    private int _shownDurationSeconds = -1;
    private double _shownFillWidth = double.NaN;

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
            if (_shownSeconds != -1 || _shownDurationSeconds != -1)
            {
                _shownSeconds = -1;
                _shownDurationSeconds = -1;
                ProgressText.Text = "--:-- / --:--";
            }

            SetFillWidth(0);
            return;
        }

        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > duration) position = duration;

        // The readout has one-second resolution, so the string is only rebuilt when the
        // displayed second actually changes.
        var seconds = (int)position.TotalSeconds;
        var durationSeconds = (int)duration.TotalSeconds;
        if (seconds != _shownSeconds || durationSeconds != _shownDurationSeconds)
        {
            _shownSeconds = seconds;
            _shownDurationSeconds = durationSeconds;
            ProgressText.Text = $"{Format(position)} / {Format(duration)}";
        }

        // Width comes from the track's measured width, so it stays correct across
        // resizes and DPI changes with no binding machinery.
        var trackWidth = ProgressTrack.ActualWidth;
        SetFillWidth(trackWidth > 0
            ? Math.Clamp(trackWidth * (position.TotalSeconds / duration.TotalSeconds), 0, trackWidth)
            : 0);
    }

    /// <summary>
    /// Assign the fill width only when it moves by at least half a DIP (or resets).
    /// Every assignment invalidates the strip's layout, and during playback the fill
    /// advances by a fraction of a pixel per frame.
    /// </summary>
    private void SetFillWidth(double width)
    {
        if (width == _shownFillWidth)
        {
            return;
        }

        if (width != 0 && Math.Abs(width - _shownFillWidth) < 0.5)
        {
            return;
        }

        _shownFillWidth = width;
        ProgressFill.Width = width;
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
    /// Colour the strip for the surface it is sitting on: the transport glyphs and
    /// the lyric palette in one pass.
    /// <para>
    /// With the readability plate disabled the strip is drawn straight onto the taskbar,
    /// where a fixed near-white lyric vanishes against a light bar. The taskbar's own
    /// colour is sampled just outside the strip — those points are not covered by this
    /// window, so they report the real backdrop — and both the glyphs and the lyrics
    /// flip to match it.
    /// </para>
    /// </summary>
    private void RefreshBackdropContrast()
    {
        // With the plate on, the backdrop is our own dark fill, so light ink is already
        // right and sampling the taskbar would answer a question we are not asking.
        if (_settings.ShowBackground)
        {
            ApplyTransportPalette(lightBar: false, halo: false);
            ApplyLyricPalette(adaptive: false, lightBar: false, sampled: false);
            return;
        }

        double? luminance = SampleBackdropLuminance();
        bool lightBar = luminance is > 0.55;

        // No reading means the ink colour is a guess, and the outline is what keeps a wrong
        // guess readable. With a reading it is not needed — the reference has no such effect.
        ApplyTransportPalette(lightBar, halo: !luminance.HasValue);
        ApplyLyricPalette(_settings.AutoAdaptColors, lightBar, luminance.HasValue);
    }

    /// <summary>
    /// Pick the lyric colours for a backdrop of the given brightness.
    /// <para>
    /// The adaptive palette is the reference implementation's: black text over a light
    /// taskbar and white over a dark one, each in two alphas — 55% for the unsung part
    /// and 90% for the sung part — so the sweep is a change of intensity in the bar's own
    /// colour rather than a second colour laid on top of it. With no reading to act on
    /// (window not positioned yet, or the sample fell outside the bar) the configured
    /// palette is used, so nothing is ever coloured from a guess.
    /// </para>
    /// </summary>
    private void ApplyLyricPalette(bool adaptive, bool lightBar, bool sampled)
    {
        var auto = adaptive && sampled;

        Brush baseBrush;
        Brush highlightBrush;
        Brush contextBrush;

        if (auto)
        {
            baseBrush = Frozen(lightBar
                ? Color.FromArgb(0x8C, 0x00, 0x00, 0x00)
                : Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
            highlightBrush = Frozen(lightBar
                ? Color.FromArgb(0xE6, 0x00, 0x00, 0x00)
                : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            contextBrush = Frozen(lightBar
                ? Color.FromArgb(0x59, 0x00, 0x00, 0x00)
                : Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            baseBrush = ParseBrush(_settings.BaseColor, Brushes.WhiteSmoke);
            highlightBrush = ParseBrush(_settings.HighlightColor, Brushes.DeepSkyBlue);
            contextBrush = ParseBrush(_settings.ContextColor, Brushes.LightGray);
        }

        foreach (var line in new[] { CurrentLine, NextLine })
        {
            line.BaseColor = baseBrush;
            line.HighlightColor = highlightBrush;
            line.ContextColor = contextBrush;

            // The offset copy only earns its place when the text colour was chosen by
            // hand and may end up light-on-light; a palette picked for contrast does not
            // need an outline on top of it.
            line.ShadowEnabled = !auto;
        }

        Diag.Log($"[overlay] palette auto={auto} lightBar={lightBar} " +
                 $"base={Describe(baseBrush)} hl={Describe(highlightBrush)}");
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>
    /// Colour the transport controls the way the reference colours its own.
    /// <para>
    /// The glyph is the surface's own colour at 70%, going to 100% under the pointer, and
    /// the play/pause pill is that same colour at 7%, going to 12% under the pointer. The
    /// contrast comes from picking a side — black over a light taskbar, white over a dark
    /// one — rather than from an outline, which is what makes the reference's controls read
    /// as part of the bar instead of as a widget stuck onto it.
    /// </para>
    /// </summary>
    private void ApplyTransportPalette(bool lightBar, bool halo)
    {
        var ink = lightBar ? Colors.Black : Colors.White;
        var outline = lightBar ? Colors.White : Colors.Black;

        _transportInk = ink;
        var haloColor = halo ? WithAlpha(outline, 0x8C) : (Color?)null;

        foreach (var button in new[] { PrevButton, PlayPauseButton, NextButton })
        {
            button.IconColor = WithAlpha(ink, 0xB3);
            button.IconHoverColor = WithAlpha(ink, 0xFF);
            button.PillColor = WithAlpha(ink, 0x12);
            button.PillHoverColor = WithAlpha(ink, 0x1F);
            button.HaloColor = haloColor;
        }

        // The groove is tinted from the same colour as the glyphs: the hard-coded white it
        // started with disappears on a light taskbar, exactly where the ink just went black.
        ProgressTrack.Background = Frozen(WithAlpha(ink, 0x33));

        ProgressText.Foreground = Frozen(WithAlpha(ink, 0xE6));
        ProgressText.Effect = halo ? HaloEffect(outline) : null;

        // The track information rides the same side as the glyphs, one step quieter, so the
        // title reads first and the artist stays a caption under it. Both get the same halo
        // as the readout: they sit directly on the taskbar whenever the strip has no backing
        // of its own, which is exactly the case that halo exists for.
        SongTitle.Foreground = Frozen(WithAlpha(ink, 0xB3));
        SongTitle.Effect = halo ? HaloEffect(outline) : null;
        SongArtist.Foreground = Frozen(WithAlpha(ink, 0x80));
        SongArtist.Effect = halo ? HaloEffect(outline) : null;
        CoverPlaceholder.Foreground = Frozen(WithAlpha(ink, 0x66));

        ApplyCoverBackground();

        SetDragHandleDragging(_dragging);

        Diag.Log($"[overlay] transport lightBar={lightBar} halo={halo} ink={ink}");
    }

    /// <summary>
    /// Brighten the drag pill while it is actually being dragged — the reference's own
    /// affordance goes from 12% to 50% for the duration of the drag.
    /// </summary>
    private void SetDragHandleDragging(bool dragging) =>
        DragHandle.Background = Frozen(WithAlpha(_transportInk, dragging ? (byte)0x80 : (byte)0x1F));


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
                // On the taskbar, only points that are genuinely on the bar count — they are
                // what the strip is drawn over. A floating strip is over whatever happens to
                // be behind it, so there the monitor is the only limit.
                if (_settings.FreePosition)
                {
                    if (x < _taskbar.MonitorBounds.Left || x > _taskbar.MonitorBounds.Right) continue;
                    if (y < _taskbar.MonitorBounds.Top || y > _taskbar.MonitorBounds.Bottom) continue;
                }
                else
                {
                    if (x < _taskbar.Bounds.Left + 2 || x > _taskbar.Bounds.Right - 2) continue;
                    if (y < _taskbar.Bounds.Top || y > _taskbar.Bounds.Bottom) continue;
                }

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
        RefreshBackdropContrast();

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

        // Switching "free position" on from the settings window has to seed the floating
        // coordinates with where the strip is now, otherwise it would jump to the origin.
        if (_settings.FreePosition)
        {
            if (!_freeSeeded && _positioned && _settings.FreeX == 0 && _settings.FreeY == 0)
            {
                _settings.FreeX = Left;
                _settings.FreeY = Top;
            }

            _freeSeeded = true;
        }
        else
        {
            _freeSeeded = false;
        }

        Diag.Log($"[overlay] ApplySettingsToVisuals showBg={_settings.ShowBackground} " +
                 $"bg={_settings.BackgroundColor} base={_settings.BaseColor} " +
                 $"hl={_settings.HighlightColor} ctx={_settings.ContextColor} " +
                 $"font={_settings.FontFamily} resolved={BundledFonts.Resolve(_settings.FontFamily)}/{_settings.OriginalFontSize} " +
                 $"showTrans={_settings.ShowTranslation} locked={_settings.Locked}");

        foreach (var line in new[] { CurrentLine, NextLine })
        {
            line.FontFamilyName = BundledFonts.Resolve(_settings.FontFamily);
            line.FontSizeValue = _settings.OriginalFontSize;
            line.TranslationFontSizeValue = _settings.TranslationFontSize;
            line.FontWeightName = _settings.FontWeight;
            line.LetterSpacing = _settings.LetterSpacing;
            line.WordHighlightEnabled = _settings.EnableWordHighlight;
        }

        // Colours are not read here: they depend on the taskbar reading taken at the
        // bottom of this method (see RefreshBackdropContrast).

        // Transport buttons are optional; hidden buttons must not capture clicks,
        // otherwise the strip would swallow taskbar input for no visible reason.
        TransportPanel.Visibility = _settings.ShowTransportControls
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Progress sits under the buttons and is independently toggleable.
        ProgressSection.Visibility = _settings.ShowSongProgress
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Track information is switchable piece by piece, so the panel itself only goes away
        // when all three of its parts are off — otherwise the cover would be dragged off the
        // strip by turning off the artist.
        CoverBox.Visibility = _settings.ShowCoverArt ? Visibility.Visible : Visibility.Collapsed;
        SongTitle.Visibility = _settings.ShowSongTitle ? Visibility.Visible : Visibility.Collapsed;
        SongArtist.Visibility = _settings.ShowSongArtist ? Visibility.Visible : Visibility.Collapsed;
        InfoPanel.Visibility = HasTrackInfo
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
            // No backdrop wanted: paint only the hit-test floor, so the taskbar shows
            // through untouched while the strip stays reachable (see HitTestFloor).
            Backdrop.Background = _settings.ShowBackground
                ? ParseBrush(_settings.BackgroundColor, new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)))
                : HitTestFloor;
        }

        Backdrop.CornerRadius = new CornerRadius(_settings.BackgroundCornerRadius);

        NextLine.Visibility = _settings.ShowContextLines ? Visibility.Visible : Visibility.Collapsed;

        Width = Math.Max(MinOverlayWidth, _settings.Width);

        ApplyControlRevealMode();

        // Last: it samples the taskbar and therefore needs the window positioned, and
        // it is the only place the lyric colours come from.
        RefreshBackdropContrast();
    }

    /// <summary>
    /// Whether the hover cluster carries any track information at all.
    /// <para>
    /// Both the reveal decision and the layout depend on it: an all-off cluster must not
    /// hold a column open, and a cluster with nothing but a cover should still be worth
    /// revealing on hover.
    /// </para>
    /// </summary>
    private bool HasTrackInfo =>
        _settings.ShowCoverArt || _settings.ShowSongTitle || _settings.ShowSongArtist;

    /// <summary>
    /// Decide whether the transport controls are always visible or revealed on hover,
    /// and put them into that state immediately.
    /// </summary>
    private void ApplyControlRevealMode()
    {
        // Hover goes by the cursor's screen position, not by mouse messages arriving at
        // this window, so it works while the strip is click-through as well. Gating the
        // reveal on Interactive used to pin the controls open for good: the strip was
        // told to show everything and never hid it again, and because the cluster stayed
        // in place the words never travelled back to the middle either.
        //
        // Track information rides the same reveal: it has no clicks of its own, and while
        // it is pinned open the words have to stay out of its way, which is exactly the
        // layout the user asked not to have.
        _hoverReveal = _settings.HoverRevealControls
                       && (_settings.ShowTransportControls || HasTrackInfo);

        // The drag pill is the strip's own affordance rather than part of the transport
        // set, so it survives the controls being turned off — but only while the strip can
        // actually be dragged, otherwise it would advertise something that does nothing.
        DragHandle.Visibility = _settings.Interactive ? Visibility.Visible : Visibility.Collapsed;

        if (!_hoverReveal)
        {
            _pointerTimer.Stop();
            TransportPanel.BeginAnimation(OpacityProperty, null);
            TransportPanel.Opacity = 1;
            InfoPanel.BeginAnimation(OpacityProperty, null);
            InfoPanel.Opacity = 1;
            DragHandle.BeginAnimation(OpacityProperty, null);
            DragHandle.Opacity = 1;

            // Pinned open means the cluster is never out of the way, so the words belong in
            // the space that is left rather than over the middle of the band.
            UpdateLyricShift(animate: false);

            Diag.Log($"[overlay] controls always visible (hoverReveal=False interactive={_settings.Interactive} show={_settings.ShowTransportControls} info={HasTrackInfo})");
            return;
        }

        Diag.Log($"[overlay] controls hover-revealed (shown={_hovered})");
        _pointerTimer.Start();
        SetControlsShown(_hovered);
    }

    /// <summary>
    /// Reveal or hide the controls according to where the pointer actually is.
    /// </summary>
    private void PollPointer()
    {
        if (!_hoverReveal || !IsVisible) return;

        var over = IsCursorOverStrip();
        if (over == _hovered) return;

        _hovered = over;
        SetControlsShown(over);
    }

    /// <summary>Is the pointer inside the strip's screen rectangle?</summary>
    private bool IsCursorOverStrip()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return false;

        // Win32 hands back physical pixels while Left/Top are DIP, so the two are only
        // comparable through the taskbar's scale (the same factor the placement uses).
        var scale = _taskbar?.Scale ?? 1.0;
        if (scale <= 0) scale = 1.0;

        // A few pixels of slack, so a pointer resting exactly on the edge does not
        // make the controls flicker.
        const double slack = 4;

        var left = (Left * scale) - slack;
        var top = (Top * scale) - slack;
        var right = ((Left + ActualWidth) * scale) + slack;
        var bottom = ((Top + ActualHeight) * scale) + slack;

        return pt.X >= left && pt.X <= right && pt.Y >= top && pt.Y <= bottom;
    }

    /// <summary>
    /// Fade the hover cluster in or out, and carry the words to the place they belong in
    /// each state.
    /// <para>
    /// The cluster's own layout never changes on hover — it keeps its column at all times,
    /// because opacity does not affect layout — so the fade re-measures nothing. What does
    /// move is the words: while the cluster is on screen they sit in the space it leaves,
    /// and while it is gone they are carried back over the middle of the band.
    /// </para>
    /// </summary>
    private void SetControlsShown(bool shown)
    {
        if (!_hoverReveal) return;

        Diag.Log($"[overlay] controls {(shown ? "revealed" : "hidden")}");

        var target = shown ? 1.0 : 0.0;

        // One duration for the whole gesture, so the cluster, the words and the drag pill
        // read as a single movement rather than as three elements arriving separately.
        var fadeMs = shown ? 120 : 180;

        var panelFade = FadeTo(target, fadeMs);

        if (shown)
        {
            TransportPanel.IsHitTestVisible = true;
        }
        else
        {
            // Invisible buttons must stop taking clicks as soon as they are gone,
            // otherwise a click on the faded-out transport area would toggle playback
            // instead of dragging the strip. Wait for the fade to finish so the
            // buttons stay usable while they are still on screen.
            panelFade.Completed += (_, _) => TransportPanel.IsHitTestVisible = false;
        }

        TransportPanel.BeginAnimation(OpacityProperty, panelFade);

        // The track information fades with the buttons it sits beside, so the cluster reads
        // as one thing arriving rather than as a cover arriving and a title following it.
        InfoPanel.BeginAnimation(OpacityProperty, FadeTo(target, fadeMs));

        // The drag pill comes and goes with the controls, on the same clock, so the two
        // read as one gesture rather than as two elements fading independently.
        DragHandle.BeginAnimation(OpacityProperty, FadeTo(target, fadeMs));

        UpdateLyricShift(animate: true);
    }

    /// <summary>
    /// An opacity ramp from wherever the property currently is to <paramref name="value"/>,
    /// with the easing every other hover transition on this strip uses.
    /// </summary>
    private static DoubleAnimation FadeTo(double value, int milliseconds) =>
        new(value, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

    /// <summary>
    /// Hand the lyric lines the room beside the hover cluster, and take it back when it leaves.
    /// <para>
    /// The lines span the whole band, so that room is expressed as an inset at their left edge:
    /// with the cluster out of sight the inset is zero and the words are centred in the strip,
    /// and while it is on screen the words are laid out in what is left beside it. Laid out, not
    /// clipped — an earlier version slid the lines aside and clipped the sliver that would have
    /// run under the cluster, which sliced the front off every line (a 128 DIP line came back
    /// 74 DIP wide). The pixel check caught it: a lyric you cannot read is worse than one that
    /// is not quite where a longer line would have put it.
    /// </para>
    /// <para>
    /// The inset is measured from the cluster itself, so it follows whatever it actually holds
    /// (cover, title, artist, buttons, readout) with no hard-coded width anywhere, and stays
    /// right when a longer title widens it.
    /// </para>
    /// </summary>
    private void UpdateLyricShift(bool animate)
    {
        // The cluster's width plus the gap it keeps on its right, in the same coordinate space
        // the lines live in: both start at the content grid's left edge. A cluster that has not
        // been measured yet reports zero, which is exactly the inset to apply until it does —
        // and a cluster with nothing left in it (every part switched off) imposes nothing, gap
        // included, so the words stay centred rather than being nudged aside by an empty box.
        var occupied = !_hoverReveal || _hovered;
        var clusterWidth = LeftCluster.ActualWidth;
        var inset = occupied && clusterWidth > 0.5 ? clusterWidth + LeftCluster.Margin.Right : 0.0;

        // NaN is the field's initial value and means "never logged yet": comparing against it
        // would be false for every target, so the first inset would go unrecorded.
        if (Diag.Enabled && (double.IsNaN(_diagInset) || Math.Abs(inset - _diagInset) > 0.5))
        {
            _diagInset = inset;
            Diag.Log($"[overlay] lyric inset={inset:F1} " +
                     $"cluster={LeftCluster.ActualWidth:F1} info={InfoPanel.ActualWidth:F1} " +
                     $"title={SongTitle.ActualWidth:F1} artist={SongArtist.ActualWidth:F1} " +
                     $"trans={TransportPanel.ActualWidth:F1} strip={ActualWidth:F1} " +
                     $"line={CurrentLine.ActualWidth:F1} occupied={occupied} hovered={_hovered}");
        }

        // Opening is a smaller move than closing (the cluster's width either way, but during a
        // reveal the eye is already following the fade), so it gets the shorter ramp.
        var duration = TimeSpan.FromMilliseconds(inset == 0 ? 160 : 240);

        foreach (var line in new[] { CurrentLine, NextLine })
        {
            if (!animate)
            {
                // Clear any ramp first: an animation outranks a local value, so assigning the
                // property while one is running would be silently ignored.
                line.BeginAnimation(KaraokeLine.ContentInsetProperty, null);
                line.ContentInset = inset;
                continue;
            }

            line.BeginAnimation(
                KaraokeLine.ContentInsetProperty,
                new DoubleAnimation(inset, duration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
    }

    /// <summary>
    /// A shadow with no offset, used to thicken thin text with the opposite colour so it
    /// stays readable where it sits straight on the taskbar.
    /// </summary>
    private static DropShadowEffect HaloEffect(Color outline) =>
        new()
        {
            Color = outline,
            ShadowDepth = 0,
            BlurRadius = 3,
            Opacity = 0.9,
        };

    /// <summary>
    /// Write the current track's title and artist into the hover cluster.
    /// <para>
    /// This runs on every media poll, so assigning the same words again is a no-op: equal
    /// text would otherwise invalidate the cluster's text layout for nothing. A different
    /// title can widen the cluster, and the place the words are carried to is measured from
    /// the cluster, so the shift is re-taken by the cluster's own size watcher afterwards.
    /// </para>
    /// </summary>
    internal void SetTrackInfo(string? title, string? artist)
    {
        var newTitle = title ?? string.Empty;
        var newArtist = artist ?? string.Empty;

        if (SongTitle.Text == newTitle && SongArtist.Text == newArtist) return;

        SongTitle.Text = newTitle;
        SongArtist.Text = newArtist;
    }

    /// <summary>
    /// Hand the strip the current track's artwork, or <c>null</c> when there is none.
    /// <para>
    /// Cheap to call with the same value or with nothing at all, because most tracks in a
    /// session either have no art or keep the same art across a whole album, and neither
    /// case should repaint the strip on every poll.
    /// </para>
    /// </summary>
    internal void SetCoverArt(ImageSource? art)
    {
        if (ReferenceEquals(art, _coverArt)) return;

        _coverArt = art;
        ApplyCoverBackground();
    }

    /// <summary>
    /// Paint the cover slot with the track's picture, or with the faint plate that stands in
    /// for one.
    /// <para>
    /// The picture goes on as the border's own background rather than as an image child: a
    /// border clips its background to its corner radius but leaves its children square, so
    /// the rounded corner the reference uses only comes out this way. The brush is kept and
    /// re-pointed instead of rebuilt, so switching tracks does not hand the render thread a
    /// new brush to freeze for no visible difference.
    /// </para>
    /// </summary>
    private void ApplyCoverBackground()
    {
        if (_coverArt is null)
        {
            _coverBrush.ImageSource = null;
            CoverBox.Background = Frozen(WithAlpha(_transportInk, 0x1F));
            CoverPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        _coverBrush.ImageSource = _coverArt;
        CoverBox.Background = _coverBrush;
        CoverPlaceholder.Visibility = Visibility.Collapsed;
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

        // A strip that has been dragged off the bar floats at its own screen position: the
        // band arithmetic below would pull it straight back onto the taskbar.
        if (_settings.FreePosition)
        {
            PlaceFree(width);
            return;
        }

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
    /// Position the floating strip. Only the monitor constrains it — the taskbar band has no
    /// say any more, which is the whole point of a free position.
    /// </summary>
    private void PlaceFree(double width)
    {
        if (_taskbar is null) return;

        var scale = _taskbar.Scale > 0 ? _taskbar.Scale : 1.0;
        var monitorLeft = _taskbar.MonitorBounds.Left / scale;
        var monitorTop = _taskbar.MonitorBounds.Top / scale;
        var monitorRight = _taskbar.MonitorRightDip;
        var monitorBottom = _taskbar.MonitorBounds.Bottom / scale;

        // Off the taskbar the strip is no longer chained to a 46 DIP band, so a user-set
        // height is honoured up to the screen itself.
        var usable = Math.Max(16, (monitorBottom - monitorTop) - (2 * EdgePadding));
        var fitted = _settings.Height > 0 ? _settings.Height : Math.Max(16, _taskbar.HeightDip - 2);
        Height = Math.Clamp(fitted, 16, usable);

        Width = width;

        // Stay fully on the monitor: a strip parked off-screen could not be grabbed again,
        // and the settings sliders only move a docked strip.
        var left = Math.Clamp(_settings.FreeX,
            monitorLeft + EdgePadding,
            Math.Max(monitorLeft + EdgePadding, monitorRight - width - EdgePadding));

        var top = Math.Clamp(_settings.FreeY,
            monitorTop + EdgePadding,
            Math.Max(monitorTop + EdgePadding, monitorBottom - Height - EdgePadding));

        // Persist what was actually applied, so a clamped position is the saved one.
        _settings.FreeX = left;
        _settings.FreeY = top;

        Left = left;
        Top = top;
        _positioned = true;

        if (_compositing == CompositingMode.ColorKey)
        {
            UpdateLayout();
            TransparencyMode.ApplyColorKey(this, _settings.BackgroundCornerRadius);
        }

        // A floating strip is a widget in its own right, so it belongs at the top of the
        // topmost band; inserting it just above the taskbar would leave every ordinary
        // window free to cover it.
        NativeMethods.SetTopmost(this);
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

    /// <summary>
    /// Point the play/pause button at what pressing it would do next: the play triangle
    /// while the player is paused, the pause bars while it is playing. The old text glyph
    /// showed both at once and could not say which.
    /// </summary>
    private void SetPlayPauseIcon(bool isPlaying)
    {
        var icon = isPlaying ? _pauseIcon : _playIcon;
        if (ReferenceEquals(PlayPauseButton.Icon, icon)) return;

        PlayPauseButton.Icon = icon;
        Diag.Log($"[overlay] transport icon -> {(isPlaying ? "pause" : "play")}");
    }

    private void RenderCore(LyricDocument? document, TimeSpan position, bool isPlaying)
    {
        SetPlayPauseIcon(isPlaying);

        if (document is null || document.IsEmpty)
        {
            SetCurrentText(string.Empty);
            CurrentLine.Translation = null;
            NextLine.Text = string.Empty;
            NextLine.Translation = null;

            // Logged once per transition: with no document this branch runs every frame,
            // and a per-frame file write costs more than the frame it describes.
            if (Diag.Enabled && _diagIndex != -1)
            {
                _diagIndex = -1;
                _diagWidth = double.NaN;
                Diag.Log("[overlay] Render -> empty document");
            }

            return;
        }

        var index = document.IndexAt(position);

        // Logged when the line or the geometry changes rather than per frame: one file write
        // per frame costs more than the frame itself and distorts what the log is read for.
        if (Diag.Enabled && (index != _diagIndex || Math.Abs(Width - _diagWidth) > 0.5))
        {
            _diagIndex = index;
            _diagWidth = Width;
            Diag.Log($"[overlay] Render pos={position:mm\\:ss\\.ff} index={index} " +
                     $"lines={document.Lines.Count} W={Width} H={Height} " +
                     $"ActualW={ActualWidth:F1} ActualH={ActualHeight:F1} " +
                     $"vis={IsVisible} left={Left:F0} top={Top:F0}");
        }
        if (index < 0)
        {
            // Pre-roll: show the first upcoming line without a sweep.
            SetCurrentText(document.Lines.Count > 0 ? document.Lines[0].Text : string.Empty);
            CurrentLine.Translation = _settings.ShowTranslation
                ? document.Lines.FirstOrDefault()?.Translation
                : null;
            CurrentLine.Syllables = null;
            CurrentLine.Progress = 0;
            SetNext(document, 1);
            return;
        }

        var line = document.Lines[index];

        SetCurrentText(line.Text);
        CurrentLine.Translation = _settings.ShowTranslation ? line.Translation : null;
        CurrentLine.Syllables = line.Syllables.Count > 0 ? line.Syllables : null;
        CurrentLine.LineStart = line.Start;
        CurrentLine.LineEnd = line.Start + line.SungDuration;
        CurrentLine.Progress = ComputeProgress(line, position);

        SetNext(document, index + 1);
    }

    /// <summary>
    /// Push the current line's text, fading the line in when it actually changed.
    /// <para>
    /// The render tick rewrites the same text a dozen times a second, so the fade is
    /// armed on the text transition only — otherwise every tick would restart it and
    /// the line would never reach full opacity.
    /// </para>
    /// </summary>
    private void SetCurrentText(string text)
    {
        var changed = !string.Equals(_renderedText, text, StringComparison.Ordinal);

        CurrentLine.Text = text;

        if (!changed) return;

        _renderedText = text;

        if (!_settings.LineTransition) return;

        // Fade from just below full: enough to read as a settle rather than a cut,
        // short enough that the first syllable is never dimmed. FillBehavior.Stop
        // hands the property back to its base value (1.0) when the animation ends.
        CurrentLine.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
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

    /// <summary>
    /// Compute the karaoke sweep progress for a line at the given position.
    /// <para>
    /// For lines with real syllable data, progress is linear over the syllable span
    /// — the per-word timing already encodes the singing rhythm.
    /// </para>
    /// <para>
    /// For lines without syllable data, progress uses the full inter-line gap with
    /// an <b>adaptive ease-out curve</b>. The curve exponent is derived from text
    /// density (characters per second of gap): dense lines (fast singing, short
    /// gap) get near-linear progress; sparse lines (few characters, long gap —
    /// likely an instrumental tail) get stronger ease-out so the sweep finishes
    /// early and holds at 100% during the tail. This replaces the old fixed-rate
    /// (3.5 syllables/sec) vocal-span estimate, which was wrong for most songs.
    /// </para>
    /// </summary>
    internal static double ComputeProgress(LyricLine line, TimeSpan position)
    {
        var span = line.SungDuration > TimeSpan.Zero ? line.SungDuration : line.Duration;
        if (span <= TimeSpan.Zero) return 1;

        var elapsed = position - line.Start;
        if (elapsed <= TimeSpan.Zero) return 0;
        if (elapsed >= span) return 1;

        var t = elapsed / span;

        // Syllable-timed lines: linear is correct — the syllable boundaries encode
        // the actual rhythm, and KaraokeLine.MeasureSungWidth maps progress to
        // per-syllable positions.
        if (line.Syllables.Count > 0) return t;

        // No syllable data: apply adaptive ease-out.
        // exponent ≈ 1.0 (linear) for dense text, up to ~1.6 for sparse text.
        var density = LineTextDensity(line.Text, span);
        var exponent = density switch
        {
            >= 3.0 => 1.05,  // dense: near-linear
            >= 1.5 => 1.2,   // medium: slight ease-out
            _ => 1.5,        // sparse: stronger ease-out for instrumental tails
        };

        return 1 - Math.Pow(1 - t, exponent);
    }

    /// <summary>
    /// Estimated character density: non-space characters per second of gap.
    /// Used only to pick the ease-out curve exponent, not to estimate duration.
    /// </summary>
    private static double LineTextDensity(string? text, TimeSpan span)
    {
        if (string.IsNullOrEmpty(text) || span <= TimeSpan.Zero) return 0;

        int chars = 0;
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch)) chars++;
        }

        return chars / span.TotalSeconds;
    }

    // ---- dragging ------------------------------------------------------

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_settings.Interactive)
        {
            return;
        }

        _dragging = true;
        _dragMoved = false;
        _dragOrigin = PointToScreen(e.GetPosition(this));
        _dragStartOffsetX = _settings.OffsetX;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        SetDragHandleDragging(true);

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
        if (scale <= 0) scale = 1.0;

        var dxDip = (current.X - _dragOrigin.X) / scale;
        var dyDip = (current.Y - _dragOrigin.Y) / scale;

        // A couple of pixels of slop keep a plain click from being read as a nudge.
        if (!_dragMoved && (Math.Abs(dxDip) + Math.Abs(dyDip)) < 3) return;
        _dragMoved = true;

        var width = Math.Max(MinOverlayWidth, _settings.Width);
        var height = ActualHeight > 0 ? ActualHeight : Math.Max(16, _taskbar?.HeightDip ?? 40);

        // While the strip is still on the bar the drag stays horizontal, so an unsteady
        // hand cannot knock the lyrics out of the taskbar by a few pixels. Pulling the
        // strip clear of the band hands it over to free placement, where it follows the
        // pointer in both axes.
        if (!_settings.FreePosition && !OverlapsBand(_dragStartTop + dyDip, height))
        {
            EnterFreePosition(_dragStartLeft + dxDip, _dragStartTop + dyDip);
        }

        if (_settings.FreePosition)
        {
            var left = _dragStartLeft + dxDip;
            var top = _dragStartTop + dyDip;

            if (_settings.SnapToEdges) Snap(ref left, ref top, width, height);

            _settings.FreeX = left;
            _settings.FreeY = top;

            // One line per real move, not per mouse event: the log flushes to disk.
            var rx = (int)Math.Round(left);
            var ry = (int)Math.Round(top);
            if (rx != _diagDragX || ry != _diagDragY)
            {
                _diagDragX = rx;
                _diagDragY = ry;
                Diag.Log($"[overlay] drag free x={rx} y={ry} w={width:F0} h={height:F0}");
            }
        }
        else
        {
            _settings.OffsetX = _dragStartOffsetX + dxDip;
        }

        PlaceOverWindow();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        _dragMoved = false;
        SetDragHandleDragging(false);
        ReleaseMouseCapture();

        // Resume the reposition timer now that the drag is finished.
        _repositionTimer.Start();

        // Dropping the strip back onto the bar docks it again, so a drag that only meant
        // to nudge it sideways cannot leave it floating by accident.
        if (_settings.FreePosition && OverlapsBand(Top, ActualHeight))
        {
            DockToTaskbar();
        }

        PlaceOverWindow();
        PositionChanged?.Invoke(this, EventArgs.Empty);
        OverlayClicked?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>
    /// True when a strip whose top sits at <paramref name="top"/> belongs to the taskbar
    /// band: either it overlaps the band, or it rests just above it, which is where
    /// "float above the taskbar" puts it.
    /// </summary>
    private bool OverlapsBand(double top, double height)
    {
        if (_taskbar is null) return true;

        var bandTop = _taskbar.TopDip;
        var bandBottom = bandTop + _taskbar.HeightDip;

        var centre = top + (height / 2.0);
        if (centre >= bandTop && centre <= bandBottom) return true;

        var gap = bandTop - (top + height);
        return gap >= -2 && gap <= 24;
    }

    /// <summary>
    /// Hand the strip over to free placement, seeded with the position it is visibly at so
    /// that switching modes cannot make it jump.
    /// </summary>
    private void EnterFreePosition(double left, double top)
    {
        _settings.FreePosition = true;
        _settings.FreeX = left;
        _settings.FreeY = top;

        Diag.Log($"[overlay] drag left the taskbar: free position x={left:F1} y={top:F1}");
    }

    /// <summary>
    /// Return to the taskbar-anchored placement while keeping the strip where it visually
    /// is: the horizontal offset is derived from the strip's own right edge, so docking
    /// does not move it.
    /// </summary>
    private void DockToTaskbar()
    {
        var anchorRight = AnchoredRightDip();
        if (!double.IsNaN(anchorRight))
        {
            _settings.OffsetX = (Left + ActualWidth) - anchorRight;
        }

        _settings.FreePosition = false;

        Diag.Log($"[overlay] docked back to the taskbar: offsetX={_settings.OffsetX:F1}");
    }

    /// <summary>Tray left edge in DIP — the anchor a docked strip hangs from.</summary>
    private double AnchoredRightDip()
    {
        if (_taskbar is null) return double.NaN;

        var scale = _taskbar.Scale > 0 ? _taskbar.Scale : 1.0;
        var device = TaskbarLocator.GetTrayLeftDevicePixels();
        if (double.IsNaN(device)) device = _taskbar.Bounds.Right;

        return device / scale;
    }

    /// <summary>
    /// Pull a dragged strip onto the nearest alignment line: the monitor edges and centre,
    /// the tray column it docks at, and the taskbar's own rows. Either edge of the strip
    /// may catch a line, whichever is closer.
    /// </summary>
    private void Snap(ref double left, ref double top, double width, double height)
    {
        if (_taskbar is null) return;

        var scale = _taskbar.Scale > 0 ? _taskbar.Scale : 1.0;
        var monitorLeft = _taskbar.MonitorBounds.Left / scale;
        var monitorTop = _taskbar.MonitorBounds.Top / scale;
        var monitorRight = _taskbar.MonitorRightDip;
        var monitorBottom = _taskbar.MonitorBounds.Bottom / scale;

        var bandTop = _taskbar.TopDip;

        var xLines = new[]
        {
            monitorLeft + EdgePadding,
            monitorRight - width - EdgePadding,
            (monitorLeft + monitorRight - width) / 2.0,
            double.NaN,
        };

        var anchoredRight = AnchoredRightDip();
        if (!double.IsNaN(anchoredRight))
        {
            // The column the strip occupies while docked, so dragging it back to roughly
            // where it came from clicks into place.
            xLines[3] = anchoredRight + _settings.OffsetX - width;
        }

        var yLines = new[]
        {
            monitorTop + EdgePadding,
            monitorBottom - height - EdgePadding,
            (monitorTop + monitorBottom - height) / 2.0,
            bandTop + ((_taskbar.HeightDip - height) / 2.0),
            bandTop - height - AboveTaskbarGap,
        };

        left = SnapAxis(left, width, xLines);
        top = SnapAxis(top, height, yLines);
    }

    private static double SnapAxis(double start, double size, double[] lines)
    {
        var best = start;
        var bestDistance = SnapDistance;

        foreach (var line in lines)
        {
            if (double.IsNaN(line)) continue;

            var distance = Math.Abs(start - line);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = line;
            }

            distance = Math.Abs((start + size) - line);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = line - size;
            }
        }

        return best;
    }
}
