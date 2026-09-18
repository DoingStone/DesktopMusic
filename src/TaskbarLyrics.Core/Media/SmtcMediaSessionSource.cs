using System.Collections.Concurrent;
using TaskbarLyrics.Core.Models;
using Windows.Media.Control;

namespace TaskbarLyrics.Core.Media;

/// <summary>
/// Reads the Windows Global System Media Transport Controls (SMTC) session.
/// <para>
/// This is the same mechanism the Windows volume flyout uses, so it works with
/// QQ Music without any injection or hooking: QQ Music publishes its track
/// metadata and playback position to the OS.
/// </para>
/// <para>
/// SMTC updates the timeline roughly once per second, so
/// <see cref="PlaybackSnapshot.ExtrapolatedPosition"/> is used by the renderer
/// to animate smoothly in between.
/// </para>
/// </summary>
public sealed class SmtcMediaSessionSource : IMediaSessionSource
{
    private readonly MediaSessionOptions _options;
    private readonly ConcurrentDictionary<string, string> _knownSources = new(StringComparer.OrdinalIgnoreCase);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public SmtcMediaSessionSource(MediaSessionOptions? options = null)
    {
        _options = options ?? new MediaSessionOptions();
    }

    public IReadOnlyList<string> KnownSourceIds =>
        _knownSources.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<PlaybackSnapshot?> GetCurrentAsync(CancellationToken ct)
    {
        var manager = await EnsureManagerAsync().ConfigureAwait(false);
        if (manager is null) return null;

        var session = PickSession(manager);
        if (session is null) return null;

        try
        {
            // WinRT IAsyncOperation has no ConfigureAwait; await it directly.
            var media = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();
            var playback = session.GetPlaybackInfo();

            var appId = session.SourceAppUserModelId ?? string.Empty;
            if (!string.IsNullOrEmpty(appId)) _knownSources[appId] = appId;

            var status = playback?.PlaybackStatus;
            var isPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var position = timeline?.Position ?? TimeSpan.Zero;
            var duration = timeline?.EndTime ?? TimeSpan.Zero;

            // The moment the player sampled `position`. Anchoring interpolation here
            // rather than to our own poll time removes the lag caused by a player
            // that updates its timeline lazily.
            var positionUpdatedAt = timeline?.LastUpdatedTime ?? default;

            // Honour playback speed when the player reports one.
            var rate = playback?.PlaybackRate;
            var playbackRate = rate is > 0.01 and < 4.0 ? rate.Value : 1.0;

            // A paused session that never reported a timeline is not useful.
            if (string.IsNullOrWhiteSpace(media?.Title) && duration <= TimeSpan.Zero) return null;

            return new PlaybackSnapshot(
                SessionId: appId,
                SourceAppId: appId,
                Title: media?.Title ?? string.Empty,
                Artist: media?.Artist ?? string.Empty,
                Album: media?.AlbumTitle ?? string.Empty,
                Duration: duration,
                Position: position,
                IsPlaying: isPlaying,
                CapturedAt: DateTimeOffset.Now)
            {
                PositionUpdatedAt = positionUpdatedAt,
                PlaybackRate = playbackRate,
            };
        }
        catch
        {
            // Sessions can vanish mid-read when the user closes the player.
            return null;
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> EnsureManagerAsync()
    {
        if (_manager is not null) return _manager;

        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync()
                .AsTask()
                .ConfigureAwait(false);
            return _manager;
        }
        catch
        {
            // WinRT projection can fail transiently; retry on the next poll.
            return null;
        }
    }

    /// <summary>
    /// Toggle play/pause on the active session. False when the session is missing
    /// or the player does not support the command.
    /// </summary>
    public async Task<bool> TryTogglePlayPauseAsync(CancellationToken ct)
    {
        var session = await GetActiveSessionAsync().ConfigureAwait(false);
        if (session is null) return false;

        try
        {
            // The manager only advertises this when the player exposes the control.
            if (!session.GetPlaybackInfo().Controls.IsPlayPauseToggleEnabled)
            {
                return false;
            }

            return await session.TryTogglePlayPauseAsync();
        }
        catch (Exception)
        {
            // A vanished session or an unsupported command: report failure.
            return false;
        }
    }

    /// <summary>Skip to the next track. False when unsupported.</summary>
    public async Task<bool> TrySkipNextAsync(CancellationToken ct)
    {
        var session = await GetActiveSessionAsync().ConfigureAwait(false);
        if (session is null) return false;

        try
        {
            if (!session.GetPlaybackInfo().Controls.IsNextEnabled)
            {
                return false;
            }

            return await session.TrySkipNextAsync();
        }
        catch (Exception)
        {
            // A vanished session or an unsupported command: report failure.
            return false;
        }
    }

    /// <summary>Skip to the previous track. False when unsupported.</summary>
    public async Task<bool> TrySkipPreviousAsync(CancellationToken ct)
    {
        var session = await GetActiveSessionAsync().ConfigureAwait(false);
        if (session is null) return false;

        try
        {
            if (!session.GetPlaybackInfo().Controls.IsPreviousEnabled)
            {
                return false;
            }

            return await session.TrySkipPreviousAsync();
        }
        catch (Exception)
        {
            // A vanished session or an unsupported command: report failure.
            return false;
        }
    }

    /// <summary>Resolve the session that transport commands should target.</summary>
    private async Task<GlobalSystemMediaTransportControlsSession?> GetActiveSessionAsync()
    {
        var manager = await EnsureManagerAsync().ConfigureAwait(false);
        return manager is null ? null : PickSession(manager);
    }

    /// <summary>
    /// Choose which session to display when several players are running.
    /// Preference order: configured priority list, then a playing session, then
    /// any session that has a track.
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? PickSession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();
        if (sessions.Count == 0) return null;

        foreach (var id in _options.PreferredSourceIds)
        {
            foreach (var s in sessions)
            {
                var appId = s.SourceAppUserModelId ?? string.Empty;
                if (Matches(appId, id)) return s;
            }
        }

        // Any allowed session that is currently playing.
        foreach (var s in sessions)
        {
            var appId = s.SourceAppUserModelId ?? string.Empty;
            if (!IsAllowed(appId)) continue;
            if (s.GetPlaybackInfo()?.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return s;
            }
        }

        foreach (var s in sessions)
        {
            var appId = s.SourceAppUserModelId ?? string.Empty;
            if (IsAllowed(appId)) return s;
        }

        return null;
    }

    private bool IsAllowed(string appId)
    {
        if (_options.AllowedSourcePrefixes.Count == 0) return true;

        foreach (var prefix in _options.AllowedSourcePrefixes)
        {
            if (Matches(appId, prefix)) return true;
        }
        return false;
    }

    private static bool Matches(string appId, string pattern) =>
        appId.Contains(pattern, StringComparison.OrdinalIgnoreCase);
}
