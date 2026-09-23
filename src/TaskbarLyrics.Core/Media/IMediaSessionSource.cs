using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Media;

/// <summary>Provides the currently active media session snapshot.</summary>
public interface IMediaSessionSource
{
    /// <summary>
    /// Read the best currently-known playback snapshot. Implementations must be
    /// cheap and safe to call several times per second.
    /// </summary>
    Task<PlaybackSnapshot?> GetCurrentAsync(CancellationToken ct);

    /// <summary>App ids observed on this machine, most recent first.</summary>
    IReadOnlyList<string> KnownSourceIds { get; }

    /// <summary>
    /// Toggle play/pause on the active session. False when the session is missing
    /// or the player does not support the command.
    /// </summary>
    Task<bool> TryTogglePlayPauseAsync(CancellationToken ct);

    /// <summary>Skip to the next track. False when unsupported.</summary>
    Task<bool> TrySkipNextAsync(CancellationToken ct);

    /// <summary>Skip to the previous track. False when unsupported.</summary>
    Task<bool> TrySkipPreviousAsync(CancellationToken ct);

    /// <summary>
    /// The active session's album art, as the encoded image bytes the player published,
    /// or null when the session has none.
    /// <para>
    /// Deliberately separate from <see cref="GetCurrentAsync"/>: this is a WinRT round
    /// trip that some players answer with a multi-megabyte image, so it is called when
    /// the track changes rather than on every poll. A player that publishes no art is
    /// normal, and answering null for it is not an error.
    /// </para>
    /// </summary>
    Task<byte[]?> TryReadAlbumArtAsync(CancellationToken ct);
}

/// <summary>
/// Which player to follow, and how strongly to trust its position reporting.
/// </summary>
public sealed class MediaSessionOptions
{
    /// <summary>
    /// App ids to prefer when several sessions are playing, in priority order.
    /// <para>
    /// Empty by default, deliberately. It used to default to QQ Music alone, and the
    /// selection loop consulted it <i>before</i> the allow-list and without requiring the
    /// session to be playing - so a paused, stale QQ Music session masked whatever was
    /// actually being listened to. A preference is a tie-break, not a filter.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> PreferredSourceIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// When set, only sessions whose app id contains one of these prefixes are followed.
    /// Empty - the default - means any session.
    /// <para>
    /// This used to default to <c>"QQMusic"</c>, which made every other player invisible:
    /// playing a song in NetEase Cloud Music produced no session at all as far as this app
    /// was concerned, so the overlay stayed empty and the lyric pipeline was never even
    /// consulted.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AllowedSourcePrefixes { get; init; } = Array.Empty<string>();
}
