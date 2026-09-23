using System.Collections.Concurrent;
using TaskbarLyrics.Core.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

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

    /// <summary>One media session as Windows currently reports it.</summary>
    public sealed record SessionInfo(
        string AppId,
        string Status,
        string Title,
        string Artist,
        string Album,
        string Genres,
        string ExactId,
        TimeSpan StartTime,
        TimeSpan EndTime,
        TimeSpan Position,
        TimeSpan MinSeek,
        TimeSpan MaxSeek,
        double Rate)
    {
        /// <summary>
        /// What the app would use as the track length. EndTime alone is what it used, and
        /// some players report the end of the whole queue there rather than the end of the
        /// current track - which shows up as an absurd duration and throws off both the
        /// progress display and the edition check that lyric matching depends on.
        /// </summary>
        public TimeSpan EndMinusStart => EndTime - StartTime;
    }

    /// <summary>
    /// Every session the OS currently exposes, regardless of what this app would choose.
    /// <para>
    /// For diagnostics only. The distinction it draws is the one that matters when lyrics
    /// do not appear: whether the player registered a session at all, or whether this app
    /// declined to follow it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SessionInfo> ListAllSessions()
    {
        try
        {
            var manager = GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();

            var result = new List<SessionInfo>();
            foreach (var s in manager.GetSessions())
            {
                var appId = s.SourceAppUserModelId ?? string.Empty;
                var playback = s.GetPlaybackInfo();
                var status = playback?.PlaybackStatus.ToString() ?? "?";
                var rate = playback?.PlaybackRate ?? 1.0;

                // Read the whole timeline, not just the end. Which field actually holds the
                // track length is exactly what needs checking when a duration looks wrong.
                var t = s.GetTimelineProperties();
                var start = t?.StartTime ?? TimeSpan.Zero;
                var end = t?.EndTime ?? TimeSpan.Zero;
                var position = t?.Position ?? TimeSpan.Zero;
                var minSeek = t?.MinSeekTime ?? TimeSpan.Zero;
                var maxSeek = t?.MaxSeekTime ?? TimeSpan.Zero;

                string title = string.Empty, artist = string.Empty, album = string.Empty;
                var genres = string.Empty;
                try
                {
                    var media = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                    title = media?.Title ?? string.Empty;
                    artist = media?.Artist ?? string.Empty;
                    album = media?.AlbumTitle ?? string.Empty;

                    // The genre field is where a plugin publishes the player's own track id.
                    genres = media?.Genres is { Count: > 0 }
                        ? string.Join(" | ", media.Genres)
                        : string.Empty;
                }
                catch
                {
                    // A session can vanish mid-read; report what we have.
                }

                var ncm = NetEaseGenreId.Match(genres);
                result.Add(new SessionInfo(
                    appId, status, title, artist, album,
                    genres.Length > 0 ? genres : "(空)",
                    ncm.Success ? ncm.Groups["id"].Value : "(无)",
                    start, end, position, minSeek, maxSeek, rate));
            }

            return result;
        }
        catch
        {
            return Array.Empty<SessionInfo>();
        }
    }

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

            // The track length is the timeline's span, not its end. A player that starts its
            // timeline at a non-zero offset would otherwise report a duration too large by
            // exactly that offset, which then skews the progress display and the edition
            // check that lyric matching depends on.
            var startTime = timeline?.StartTime ?? TimeSpan.Zero;
            var endTime = timeline?.EndTime ?? TimeSpan.Zero;
            var duration = endTime > startTime ? endTime - startTime : TimeSpan.Zero;

            // The moment the player sampled `position`. Anchoring interpolation here
            // rather than to our own poll time removes the lag caused by a player
            // that updates its timeline lazily.
            var positionUpdatedAt = timeline?.LastUpdatedTime ?? default;

            // Honour playback speed when the player reports one.
            var rate = playback?.PlaybackRate;
            var playbackRate = rate is > 0.01 and < 4.0 ? rate.Value : 1.0;

            // A paused session that never reported a timeline is not useful.
            if (string.IsNullOrWhiteSpace(media?.Title) && duration <= TimeSpan.Zero) return null;

            // A player can publish its own track identifier through the genre field. The
            // InfLink-rs plugin does this for NetEase Cloud Music as "NCM-{id}", which lets
            // the lyric lookup fetch that exact song instead of searching for it.
            string? exactId = null;
            if (media?.Genres is { Count: > 0 })
            {
                foreach (var genre in media.Genres)
                {
                    var match = NetEaseGenreId.Match(genre ?? string.Empty);
                    if (match.Success)
                    {
                        exactId = match.Groups["id"].Value;
                        break;
                    }
                }
            }

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
                ExactSourceId = exactId,
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

    /// <summary>
    /// Read the active session's album art.
    /// <para>
    /// The thumbnail is the same image Windows itself shows in the volume flyout, so it
    /// needs no per-player handling: a player that publishes art to SMTC gets its art on
    /// the strip, and one that publishes none gets null.
    /// </para>
    /// </summary>
    public async Task<byte[]?> TryReadAlbumArtAsync(CancellationToken ct)
    {
        try
        {
            var session = await GetActiveSessionAsync().ConfigureAwait(false);
            if (session is null) return null;

            var media = await session.TryGetMediaPropertiesAsync().AsTask(ct).ConfigureAwait(false);

            // Not disposable: the reference is a handle the stream is opened from, and the
            // stream itself is what has to be released.
            var thumbnail = media?.Thumbnail;
            if (thumbnail is null) return null;

            using var stream = await thumbnail.OpenReadAsync().AsTask(ct).ConfigureAwait(false);

            var size = stream.Size;

            // A cover is tens to hundreds of kilobytes. Anything past this cap is not a
            // cover, and reading it would cost more than the strip could ever show.
            if (size == 0 || size > MaxAlbumArtBytes) return null;

            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)size).AsTask(ct).ConfigureAwait(false);

            var bytes = new byte[(int)size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception)
        {
            // A session that vanished mid-read, or a thumbnail the player will not open:
            // "no cover" is a normal answer here rather than a failure worth surfacing.
            return null;
        }
    }

    /// <summary>Upper bound on a thumbnail this app will read; see the member above.</summary>
    private const long MaxAlbumArtBytes = 8 * 1024 * 1024;

    /// <summary>Resolve the session that transport commands should target.</summary>
    private async Task<GlobalSystemMediaTransportControlsSession?> GetActiveSessionAsync()
    {
        var manager = await EnsureManagerAsync().ConfigureAwait(false);
        return manager is null ? null : PickSession(manager);
    }

    /// <summary>
    /// Names of running audio players that have <b>not</b> registered a media session.
    /// <para>
    /// For telling two very different situations apart: nothing is playing, versus a player
    /// is playing but never told Windows. NetEase Cloud Music is the second case - its Win32
    /// build does not implement the system media transport controls at all, so no amount of
    /// polling will find it and the remedy is a plugin such as BetterNCM with InfLink-rs.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> RunningPlayersWithoutSession()
    {
        // Players that publish SMTC when they are working correctly. Anything here that is
        // running while no session exists is worth naming to the user.
        string[] known =
        {
            "cloudmusic", "QQMusic", "KuGou", "Spotify", "foobar2000", "PotPlayer",
            "MusicBee", "AIMP", "vlc",
        };

        var found = new List<string>();
        foreach (var name in known)
        {
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0) found.Add(name);
            }
            catch
            {
                // Process enumeration can fail on permissions; skip that name.
            }
        }

        return found;
    }

    /// <summary>
    /// Whether a session looks like music rather than a video, a browser tab or a lecture.
    /// <para>
    /// This is a lyrics app: following a video file is never useful, and doing so is worse
    /// than following nothing, because the overlay then shows a progress bar and a duration
    /// for something that has no lyrics at all. Measured case: with a paused music player
    /// and a browser playing a 43-minute lecture video, the app latched onto the video -
    /// which is where the absurd "43:04" duration and the missing lyrics both came from.
    /// </para>
    /// <para>
    /// Three signals, in order of strength: a known music player by name; a media file
    /// extension in the title, which means a file rather than a track; and a missing artist,
    /// which music tracks almost always carry and video files almost never do.
    /// </para>
    /// </summary>
    public static bool LooksLikeMusic(string appId, string title, string artist)
    {
        if (MusicPlayerIds.Any(id => Matches(appId, id))) return true;

        // A filename in the title is a file, not a track.
        if (MediaFileExtensions.Any(ext => title.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Video players and browsers report no artist; music players essentially always do.
        return !string.IsNullOrWhiteSpace(artist);
    }

    /// <summary>Apps that are certainly music players, whatever their session looks like.</summary>
    private static readonly string[] MusicPlayerIds =
    {
        "QQMusic", "cloudmusic", "KuGou", "Spotify", "foobar2000", "MusicBee",
        "AIMP", "aimp", "Mediamonkey", "groove", "ZuneMusic", "iTunes", "AppleMusic",
    };

    private static readonly string[] MediaFileExtensions =
    {
        ".mp4", ".mkv", ".avi", ".flv", ".webm", ".mov", ".wmv", ".m4v", ".ts", ".rmvb",
    };

    /// <summary>
    /// NetEase song id carried in the genre field as <c>NCM-{id}</c>, published by the
    /// InfLink-rs plugin. Anchored to the whole value so an unrelated genre that happens to
    /// contain the letters is not mistaken for one.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex NetEaseGenreId =
        new(@"^\s*NCM-(?<id>\d+)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Choose which session to display when several players are running.
    /// <para>
    /// A session that is actually <b>playing</b> wins. The configured preference only breaks
    /// ties between playing sessions, so a paused player can never mask the one being
    /// listened to. Previously the preference list was consulted first, unconditionally and
    /// ahead of the allow-list, which - combined with an allow-list of "QQMusic" - meant the
    /// app followed QQ Music and nothing else.
    /// </para>
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? PickSession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();
        if (sessions.Count == 0) return null;

        // Record every session, not just the chosen one. KnownSourceIds is what diagnostics
        // report, and recording only the winner made "no known sessions" indistinguishable
        // from "no sessions exist" - which is exactly the wrong conclusion to draw when a
        // player is being filtered out.
        foreach (var s in sessions)
        {
            var id = s.SourceAppUserModelId ?? string.Empty;
            if (id.Length > 0) _knownSources[id] = id;
        }

        // Only music sessions are ever considered, whatever else is playing.
        var music = new List<(GlobalSystemMediaTransportControlsSession Session, bool Playing)>();
        foreach (var s in sessions)
        {
            var appId = s.SourceAppUserModelId ?? string.Empty;
            if (!IsAllowed(appId)) continue;

            string title = string.Empty, artist = string.Empty;
            try
            {
                var media = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                title = media?.Title ?? string.Empty;
                artist = media?.Artist ?? string.Empty;
            }
            catch
            {
                // A session can vanish mid-read; treat it as unreadable.
            }

            if (!LooksLikeMusic(appId, title, artist)) continue;

            music.Add((s, IsPlaying(s)));
        }

        if (music.Count == 0) return null;

        // A session that is actually playing wins. The configured preference only breaks ties
        // between those, so a paused player cannot mask the one being listened to.
        var playing = music.Where(m => m.Playing).Select(m => m.Session).ToArray();

        foreach (var id in _options.PreferredSourceIds)
        {
            foreach (var s in playing)
            {
                if (Matches(s.SourceAppUserModelId ?? string.Empty, id)) return s;
            }
        }

        if (playing.Length > 0) return playing[0];

        // Nothing is playing: fall back to a paused music session so its lyrics still show.
        foreach (var id in _options.PreferredSourceIds)
        {
            foreach (var m in music)
            {
                if (Matches(m.Session.SourceAppUserModelId ?? string.Empty, id)) return m.Session;
            }
        }

        return music[0].Session;
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session) =>
        session.GetPlaybackInfo()?.PlaybackStatus ==
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

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
