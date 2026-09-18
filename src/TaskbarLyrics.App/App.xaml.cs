using System.Windows;
using System.Windows.Threading;
using TaskbarLyrics.App.Configuration;
using TaskbarLyrics.App.Interop;
using TaskbarLyrics.App.Views;
using TaskbarLyrics.Core.Lyrics;
using TaskbarLyrics.Core.Lyrics.Providers;
using TaskbarLyrics.Core.Media;
using TaskbarLyrics.Core.Models;
namespace TaskbarLyrics.App;

/// <summary>
/// Application shell: owns the media poller, the lyric resolver, the overlay
/// window and the tray icon, and keeps them in sync.
/// </summary>
public partial class App : Application
{
    /// <summary>How often the playback position is sampled for rendering.</summary>
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(80);

    /// <summary>How often SMTC itself is polled for track/metadata changes.</summary>
    private static readonly TimeSpan MediaInterval = TimeSpan.FromMilliseconds(400);

    private AppSettings _settings = null!;
    private SmtcMediaSessionSource _media = null!;
    private LyricResolver _resolver = null!;
    private OverlayWindow _overlay = null!;
    private TrayIcon _tray = null!;
    private MediaHotkeys _hotkeys = null!;
    private CompositingMode _compositing;
    private bool _compositingResolved;
    private bool _initialVerificationPending;

    private DispatcherTimer _renderTimer = null!;
    private DispatcherTimer _mediaTimer = null!;

    private PlaybackSnapshot _track = PlaybackSnapshot.Empty;
    private LyricDocument? _document;
    private bool _resolving;
    private string _lastResolvedKey = string.Empty;

    /// <summary>
    /// Newest resolve request seen while one was already running, so a mid-lookup
    /// track change is handled instead of dropped.
    /// </summary>
    private PlaybackSnapshot _pendingTrack = PlaybackSnapshot.Empty;
    private string _pendingKey = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless verification mode: exercise the settings code path and exit
        // without creating any window or tray icon.
        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = SettingsSelfTest.Run();
            Shutdown(Environment.ExitCode);
            return;
        }

        // Open the settings UI directly (used by the Start-menu-style shortcut).
        bool openSettingsOnStart = e.Args.Any(a =>
            string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase));

        // Started silently (logon autostart or an explicit --tray): do not show UI.
        bool startHidden = e.Args.Any(a =>
            string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Diag.Reset();
        Diag.Log($"[app] startup os={Environment.OSVersion} 64bit={Environment.Is64BitProcess} " +
                 $"dpiAwareness={DpiInfo.Describe()}");

        // A fault in one dialog must not tear down the whole overlay and tray.
        // Log it, keep running, and tell the user which action failed.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _settings = AppSettings.Load();
        _media = new SmtcMediaSessionSource();

        BuildResolver();

        _compositing = TransparencyMode.Resolve();
        _overlay = CreateOverlay();

        // The transparency self-check paints the window with a sentinel colour to
        // prove the pixels reach the screen. Keep the overlay hidden until that
        // finishes, otherwise that colour flashes in the taskbar at startup.
        _initialVerificationPending =
            !TransparencyMode.IsForced && _compositing == CompositingMode.PerPixelAlpha;

        if (!_initialVerificationPending)
        {
            _overlay.Show();
        }

        SetupTray();

        // Playback shortcuts, registered against the overlay's live handle.
        _hotkeys = new MediaHotkeys(
            window: _overlay,
            togglePlayPause: TogglePlayPauseAsync,
            next: SkipNextAsync,
            previous: SkipPreviousAsync,
            toggleOverlay: ToggleVisibility);

        _hotkeys.Attach();
        _hotkeys.Apply(_settings);

        if (_hotkeys.UnavailableShortcuts.Count > 0)
        {
            Diag.Log("[app] some hotkeys unavailable: " +
                     string.Join(", ", _hotkeys.UnavailableShortcuts));
        }

        _mediaTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = MediaInterval,
        };
        _mediaTimer.Tick += async (_, _) => await PollMediaAsync();
        _mediaTimer.Start();

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = RenderInterval,
        };
        _renderTimer.Tick += (_, _) => RenderTick();
        _renderTimer.Start();

        // Populate immediately rather than waiting for the first tick.
        _ = PollMediaAsync();

        // Verify that the window's transparency actually reaches the screen, and
        // fall back to a colour-keyed window if it does not. Runs synchronously
        // (it pumps its own dispatcher frames) so the sentinel colour is gone
        // before the overlay is ever revealed.
        VerifyCompositing();

        if (openSettingsOnStart || !startHidden)
        {
            // Show the window on a normal launch so double-clicking the exe
            // visibly does something. The overlay is already up by then.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(OpenSettings));
        }

        // Diagnostic: exercise the real exit path after a delay so shutdown
        // timing can be measured without a human clicking the tray menu.
        if (int.TryParse(Environment.GetEnvironmentVariable("TBL_AUTOEXIT"), out var autoExitMs) &&
            autoExitMs > 0)
        {
            Diag.Log($"[diag] auto-exit scheduled in {autoExitMs} ms");
            var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(autoExitMs),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Diag.Log("[diag] auto-exit firing");
                ExitApp();
            };
            timer.Start();
        }
    }

    private OverlayWindow CreateOverlay()
    {
        var overlay = new OverlayWindow(_settings, _compositing);

        overlay.PositionChanged += (_, _) =>
        {
            // Persist, then push the new offset into the settings dialog if it is
            // open. The dialog edits a snapshot, so without this hand-off its stale
            // offset would overwrite the drag on the next edit and the strip would
            // jump back to where it started.
            SaveSettings();
            SettingsWindow.Current?.AdoptPosition(_settings.OffsetX, _settings.OffsetY);
        };

        // Transport buttons live inside the lyric strip, so playback can be
        // controlled from the taskbar itself.
        overlay.PlayPauseRequested += (_, _) => _ = TogglePlayPauseAsync();
        overlay.NextRequested += (_, _) => _ = SkipNextAsync();
        overlay.PreviousRequested += (_, _) => _ = SkipPreviousAsync();

        return overlay;
    }

    /// <summary>
    /// Verify that the overlay's transparency actually reaches the screen, and
    /// recreate it colour-keyed when it does not.
    /// <para>
    /// Runs while the overlay is still hidden: the check paints a sentinel colour
    /// onto the window, and doing that on a visible window produced a brief pink
    /// flash in the taskbar at every startup.
    /// </para>
    /// </summary>
    private void VerifyCompositing()
    {
        if (_compositingResolved || TransparencyMode.IsForced) return;
        if (_compositing != CompositingMode.PerPixelAlpha) return;

        bool ok;
        try
        {
            ok = _overlay.VerifyCompositing();
        }
        catch (Exception ex)
        {
            Diag.Log($"[app] verify EXCEPTION {ex.Message}");
            ok = true;
        }

        _compositingResolved = true;

        if (ok)
        {
            Diag.Log("[app] per-pixel alpha verified; revealing overlay");
            RevealOverlayIfEnabled();
            return;
        }

        Diag.Log("[app] per-pixel alpha NOT composited; switching to colour-key");

        // Recreate the window, because AllowsTransparency cannot be changed once
        // the handle exists.
        try
        {
            var old = _overlay;
            _compositing = CompositingMode.ColorKey;

            var replacement = CreateOverlay();
            _overlay = replacement;
            old.Close();

            RevealOverlayIfEnabled();
            _tray.SetTooltip("任务栏歌词 · 已切换兼容渲染模式");
        }
        catch (Exception ex)
        {
            Diag.Log($"[app] colour-key switch EXCEPTION {ex}");
            RevealOverlayIfEnabled();
        }
    }

    /// <summary>Show the overlay unless the user has it switched off.</summary>
    private void RevealOverlayIfEnabled()
    {
        _initialVerificationPending = false;

        if (!_settings.Visible) return;
        if (_overlay.IsVisible) return;

        _overlay.Show();
        _overlay.PlaceOverWindow();
    }

    /// <summary>
    /// Keep the app alive when a UI action throws. The overlay runs unattended in
    /// the taskbar, so losing it (and the tray icon) because a dialog misbehaved
    /// would be far worse than surfacing the error and continuing.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Diag.Log($"[app] UNHANDLED {e.Exception}");
        e.Handled = true;

        try
        {
            MessageBox.Show(
                $"操作时发生错误，程序会继续运行。\n\n{e.Exception.Message}\n\n" +
                $"详细信息已写入：\n{System.IO.Path.Combine(System.IO.Path.GetTempPath(), "taskbar-lyrics-diag.log")}",
                "任务栏歌词", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // Never let error reporting itself throw.
        }
    }

    private void BuildResolver()
    {
        var providers = new List<ILyricProvider>();

        if (_settings.EnableQqMusic) providers.Add(new QqMusicProvider());
        if (_settings.EnableNetEase) providers.Add(new NetEaseProvider());
        if (_settings.EnableLrclib) providers.Add(new LrclibProvider());

        _resolver = new LyricResolver(providers);
    }

    private void SetupTray()
    {
        _tray = new TrayIcon();

        // Double-click toggles; a single click is deliberately not wired, because
        // the overlay sits over the notification area and stray single clicks on
        // the taskbar could otherwise hide the lyrics.
        _tray.LeftDoubleClickAction = ToggleVisibility;

        _tray.SetMenu(new[]
        {
            new TrayIcon.TrayMenuItem("设置…", OpenSettings),
            TrayIcon.TrayMenuItem.Separator(),
            new TrayIcon.TrayMenuItem("显示任务栏歌词", ToggleVisibility,
                () => _settings.Visible),
            TrayIcon.TrayMenuItem.Separator(),
            new TrayIcon.TrayMenuItem("锁定位置（点击穿透）", ToggleLocked,
                () => _settings.Locked),
            new TrayIcon.TrayMenuItem("恢复默认位置", ResetPosition),
            TrayIcon.TrayMenuItem.Separator(),
            new TrayIcon.TrayMenuItem("调整歌词偏移 (提前 0.5s)", () => AdjustOffset(-500)),
            new TrayIcon.TrayMenuItem("调整歌词偏移 (延后 0.5s)", () => AdjustOffset(+500)),
            new TrayIcon.TrayMenuItem("重置歌词偏移", () => AdjustOffset(0, reset: true)),
            TrayIcon.TrayMenuItem.Separator(),
            new TrayIcon.TrayMenuItem("重新匹配当前歌曲歌词", ForceRematch),
            new TrayIcon.TrayMenuItem("清除歌词缓存", ClearCache),
            TrayIcon.TrayMenuItem.Separator(),
            new TrayIcon.TrayMenuItem("打开设置文件", OpenSettingsFile),
            new TrayIcon.TrayMenuItem("退出", ExitApp),
        });
    }

    /// <summary>
    /// Open the settings UI. The dialog edits a working copy and pushes changes
    /// live, so the taskbar reflects each edit immediately.
    /// </summary>
    private void OpenSettings()
    {
        SettingsWindow.ShowOrFocus(
            owner: _overlay,
            currentSettings: () => _settings,
            applyLive: ApplySettingsLive,
            save: SaveSettingsFromDialog,
            rematch: ForceRematch,
            clearCache: ClearCache,
            showDiagnostics: ShowLyricDiagnostics,
            quit: ExitApp,
            transportPlayPause: () => _ = TogglePlayPauseAsync(),
            transportNext: () => _ = SkipNextAsync(),
            transportPrevious: () => _ = SkipPreviousAsync());
    }

    /// <summary>
    /// Push dialog edits into the running app without persisting them. Provider
    /// toggles rebuild the resolver because the provider list is fixed at
    /// construction time.
    /// </summary>
    private void ApplySettingsLive(AppSettings updated)
    {
        // Provider selection is structural: rebuild and re-resolve if it changed.
        bool providersChanged =
            updated.EnableQqMusic != _settings.EnableQqMusic ||
            updated.EnableNetEase != _settings.EnableNetEase ||
            updated.EnableLrclib != _settings.EnableLrclib;

        // Position memory and autostart are owned by the app, not the dialog.
        updated.Positions = _settings.Positions;
        updated.StartWithWindows = _settings.StartWithWindows;

        _settings = updated;

        _overlay.UpdateSettings(_settings);

        // Re-register shortcuts so edited bindings take effect immediately.
        _hotkeys?.Apply(_settings);

        if (providersChanged)
        {
            BuildResolver();
            ForceRematch();
        }

        if (!_settings.Visible) _overlay.Hide();
        else if (!_overlay.IsVisible) _overlay.Show();
    }

    /// <summary>Persist settings coming from the dialog and refresh the tray tooltip.</summary>
    private void SaveSettingsFromDialog(AppSettings updated)
    {
        ApplySettingsLive(updated);
        _settings.Save();
    }

    /// <summary>
    /// Show what the resolver found for the current track — the practical way to
    /// diagnose a wrong-lyrics report.
    /// </summary>
    private void ShowLyricDiagnostics()
    {
        var track = _track;
        if (!track.HasTrack)
        {
            MessageBox.Show("当前没有检测到正在播放的歌曲。", "歌词匹配详情",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"曲目：{track.Title} - {track.Artist}");
        lines.AppendLine($"专辑：{track.Album}");
        lines.AppendLine($"时长：{track.Duration:hh\\:mm\\:ss}    来源：{track.SourceAppId}");
        lines.AppendLine();

        if (_document is null || _document.IsEmpty)
        {
            lines.AppendLine("未找到歌词。");
            lines.AppendLine();
            lines.AppendLine("可尝试：清空歌词缓存后重新匹配，或在「歌词来源」中启用更多来源。");
        }
        else
        {
            lines.AppendLine($"歌词来源：{DescribeSource(_document.Source)}（{_document.SourceDetail}）");
            lines.AppendLine($"行数：{_document.Lines.Count}    " +
                             $"逐字时间轴：{(_document.HasRealWordTiming ? "有" : "无")}");

            var translated = _document.Lines.Count(l => !string.IsNullOrEmpty(l.Translation));
            lines.AppendLine($"含翻译的行数：{translated}");
            lines.AppendLine();
            lines.AppendLine("前 8 行预览：");
            foreach (var line in _document.Lines.Take(8))
            {
                lines.AppendLine($"  [{line.Start:mm\\:ss\\.ff}] {line.Text}");
                if (!string.IsNullOrEmpty(line.Translation))
                {
                    lines.AppendLine($"            译：{line.Translation}");
                }
            }
        }

        MessageBox.Show(lines.ToString(), "歌词匹配详情",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Poll SMTC and kick off a lyric lookup when the track changes.</summary>
    private async Task PollMediaAsync()
    {
        // Deliberately NOT gated on _resolving. This poll is the only source of
        // playback position, and a lyric lookup takes 0.8-3 s; blocking the poll for
        // that long meant a seek or track change during the lookup went unnoticed and
        // the lyrics stayed on the old timeline until it finished.
        try
        {
            var track = await _media.GetCurrentAsync(CancellationToken.None);
            if (track is null || !track.HasTrack)
            {
                _track = PlaybackSnapshot.Empty;
                _document = null;
                _lastResolvedKey = string.Empty;
                _tray.SetTooltip("任务栏歌词 · 无播放");
                return;
            }

            // Always refresh: this carries the position and its anchor timestamp.
            _track = track;

            var key = track.CacheKey;
            if (key == _lastResolvedKey) return;

            _lastResolvedKey = key;
            _document = null;
            _tray.SetTooltip($"任务栏歌词 · {track.Title} - {track.Artist}");

            // Fire and forget so a slow lookup cannot stall the polling loop.
            _ = ResolveAsync(track, key);
        }
        catch
        {
            // Never let a polling failure tear down the app.
        }
    }

    /// <summary>
    /// Resolve lyrics, coalescing requests.
    /// <para>
    /// Only one lookup runs at a time, but a track change that arrives mid-lookup is
    /// remembered and handled immediately afterwards. Without that, the newest track
    /// would be silently dropped because <c>_lastResolvedKey</c> had already moved on.
    /// </para>
    /// </summary>
    private async Task ResolveAsync(PlaybackSnapshot track, string key)
    {
        if (_resolving)
        {
            _pendingTrack = track;
            _pendingKey = key;
            return;
        }

        _resolving = true;

        try
        {
            while (true)
            {
                _pendingKey = string.Empty;

                var resolution = await _resolver.ResolveAsync(track, CancellationToken.None);

                // Discard a result superseded while it was in flight.
                if (key == _lastResolvedKey)
                {
                    _document = resolution.Document;

                    _tray.SetTooltip(resolution.Found
                        ? $"任务栏歌词 · {track.Title} - {track.Artist}\n歌词来源: {DescribeSource(resolution.Source)}"
                        : $"任务栏歌词 · {track.Title} - {track.Artist}\n未找到歌词");
                }

                // A newer request arrived while resolving: handle it now.
                if (_pendingKey.Length == 0) break;

                track = _pendingTrack;
                key = _pendingKey;
            }
        }
        catch
        {
            // Leave _document null; the next track change retries.
        }
        finally
        {
            _resolving = false;
        }
    }

    private static string DescribeSource(LyricSourceKind kind) => kind switch
    {
        LyricSourceKind.QqMusic => "QQ音乐",
        LyricSourceKind.NetEase => "网易云音乐",
        LyricSourceKind.Kugou => "酷狗音乐",
        LyricSourceKind.Lrclib => "LRCLIB",
        LyricSourceKind.LocalFile => "本地文件",
        LyricSourceKind.Embedded => "内嵌歌词",
        _ => "未知",
    };

    /// <summary>Per-frame render from the extrapolated playback position.</summary>
    private void RenderTick()
    {
        if (!_settings.Visible)
        {
            if (_overlay.IsVisible) _overlay.Hide();
            return;
        }

        if (!_overlay.IsVisible) _overlay.Show();

        if (!_track.HasTrack)
        {
            _overlay.SetProgress(TimeSpan.Zero, TimeSpan.Zero);
            _overlay.Render(null, TimeSpan.Zero, false);
            return;
        }

        // Song progress reflects playback regardless of lyric state, so it is updated
        // before the lyric early-returns below. The lyric sync offset is deliberately
        // not applied: that shifts lyrics relative to the voice, not the clock.
        _overlay.SetProgress(_track.ExtrapolatedPosition(DateTimeOffset.Now), _track.Duration);

        if (!_track.IsPlaying && !_settings.ShowWhenPaused)
        {
            _overlay.Render(null, TimeSpan.Zero, false);
            return;
        }

        if (_document is null || _document.IsEmpty)
        {
            if (_settings.HideWhenNoLyrics)
            {
                _overlay.Render(null, TimeSpan.Zero, false);
                return;
            }

            // Show the track title while lyrics are still being resolved.
            _overlay.Render(new LyricDocument(new[]
            {
                new LyricLine
                {
                    Start = TimeSpan.Zero,
                    End = TimeSpan.FromMinutes(10),
                    Text = $"{_track.Title} - {_track.Artist}",
                },
            }, LyricSourceKind.None), TimeSpan.Zero, _track.IsPlaying);
            return;
        }

        var position = _track.ExtrapolatedPosition(DateTimeOffset.Now)
                       + TimeSpan.FromMilliseconds(_settings.GlobalOffsetMs);

        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        _overlay.Render(_document, position, _track.IsPlaying);
    }

    // ---- playback transport --------------------------------------------

    /// <summary>
    /// Commands are issued through the same SMTC session we read metadata from, so
    /// they land on whichever player is currently driving the lyrics.
    /// </summary>
    private Task TogglePlayPauseAsync() => RunTransportAsync(
        _media.TryTogglePlayPauseAsync, "暂停/播放");

    private Task SkipNextAsync() => RunTransportAsync(_media.TrySkipNextAsync, "下一首");

    private Task SkipPreviousAsync() => RunTransportAsync(_media.TrySkipPreviousAsync, "上一首");

    /// <summary>
    /// Run one transport command and surface the result. Failures are reported
    /// rather than swallowed, because "the button did nothing" is otherwise
    /// impossible for the user to diagnose.
    /// </summary>
    private async Task RunTransportAsync(Func<CancellationToken, Task<bool>> command, string label)
    {
        bool ok;
        try
        {
            ok = await command(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Diag.Log($"[transport] {label} EXCEPTION {ex.Message}");
            ok = false;
        }

        Diag.Log($"[transport] {label} -> {(ok ? "ok" : "unsupported/failed")}");

        if (!ok)
        {
            _tray.SetTooltip($"任务栏歌词 · {label}：当前播放器不支持或未在播放");
        }
        else
        {
            // Reflect the new state on the next poll rather than waiting for the
            // regular interval, so the lyrics update promptly.
            _ = PollMediaAsync();
        }
    }

    // ---- tray commands -------------------------------------------------

    private void ToggleVisibility()
    {
        _settings.Visible = !_settings.Visible;
        if (!_settings.Visible) _overlay.Hide();
        else _overlay.Show();
        SaveSettings();
    }

    private void ToggleLocked()
    {
        _settings.Locked = !_settings.Locked;
        _overlay.UpdateSettings(_settings);
        SaveSettings();
    }

    private void ResetPosition()
    {
        _settings.OffsetX = 0;
        _settings.OffsetY = 0;
        _settings.Width = 460;
        _overlay.UpdateSettings(_settings);
        SaveSettings();
    }

    private void AdjustOffset(int deltaMs, bool reset = false)
    {
        _settings.GlobalOffsetMs = reset ? 0 : _settings.GlobalOffsetMs + deltaMs;
        SaveSettings();
    }

    private void ForceRematch()
    {
        _lastResolvedKey = string.Empty;
        _document = null;
        _ = PollMediaAsync();
    }

    private void ClearCache()
    {
        _resolver.ClearCache();
        ForceRematch();
    }

    private void OpenSettingsFile()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.ConfigDirectory);
            if (!System.IO.File.Exists(AppSettings.ConfigPath))
            {
                _settings.Save();
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppSettings.ConfigPath,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Opening the file is a convenience; ignore shell failures.
        }
    }

    private void ExitApp()
    {
        // Timed because "exit is slow" is otherwise impossible to attribute.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string step) => Diag.Log($"[exit] {step} at {clock.ElapsedMilliseconds} ms");

        Diag.Log($"[exit] on UI thread = {Dispatcher.CheckAccess()} " +
                 $"(thread {Environment.CurrentManagedThreadId})");

        try
        {
            Mark("begin");

            _renderTimer.Stop();
            Mark("render timer stopped");

            _mediaTimer.Stop();
            Mark("media timer stopped");

            // Release the shortcuts before anything else disappears, so they are
            // immediately usable by other applications.
            try { _hotkeys?.Unregister(); } catch { }
            Mark("hotkeys unregistered");

            SaveSettings();
            Mark("settings saved");

            // Settings dialog can hold a reference to the overlay; close it first
            // so it cannot re-apply settings mid-shutdown.
            try { SettingsWindow.Current?.Close(); } catch { }
            Mark("settings window closed");

            _tray.Dispose();
            Mark("tray disposed");

            _overlay.Close();
            Mark("overlay closed");
        }
        catch (Exception ex)
        {
            Diag.Log($"[exit] EXCEPTION {ex.Message}");
        }

        // Everything user-visible is already torn down (tray icon removed, overlay
        // closed, settings written).
        //
        // WPF's Shutdown() then unwinds the dispatcher and the composition threads,
        // which is far cheaper than racing it with a forced exit. The watchdog is
        // only a hang-guard: it fires well after the normal path (measured ~0.6 s)
        // so a wedged dispatcher can never leave a stuck process behind.
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(2500);
            Diag.Log($"[exit] WATCHDOG fired at {clock.ElapsedMilliseconds} ms " +
                     "(normal shutdown did not complete)");
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "TaskbarLyrics.ExitWatchdog",
        };
        watchdog.Start();

        Diag.Log($"[exit] calling Shutdown at {clock.ElapsedMilliseconds} ms");
        Shutdown();
        Diag.Log($"[exit] Shutdown returned at {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Persist settings, tolerating shutdown paths where startup never got far
    /// enough to load them (for example the headless <c>--selftest</c> mode).
    /// </summary>
    private void SaveSettings() => _settings?.Save();

    protected override void OnExit(ExitEventArgs e)
    {
        SaveSettings();
        base.OnExit(e);
    }

    /// <summary>
    /// Catch termination paths that bypass <see cref="OnExit"/> (logoff, Task
    /// Manager "End task", or a crash) so the user's position and tray choices
    /// survive. Session ending is the only one reliably delivered.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        SaveSettings();
        base.OnSessionEnding(e);
    }
}
