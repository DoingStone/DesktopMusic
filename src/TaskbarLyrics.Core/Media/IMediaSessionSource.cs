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
}

/// <summary>
/// Which player to follow, and how strongly to trust its position reporting.
/// </summary>
public sealed class MediaSessionOptions
{
    /// <summary>App ids we prefer, in priority order.</summary>
    public IReadOnlyList<string> PreferredSourceIds { get; init; } = new[]
    {
        "QQMusic.exe",
    };

    /// <summary>
    /// When set, only sessions whose app id matches one of these prefixes are
    /// followed. Empty means "any session".
    /// </summary>
    public IReadOnlyList<string> AllowedSourcePrefixes { get; init; } = new[]
    {
        "QQMusic",
    };
}
