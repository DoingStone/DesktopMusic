using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Lyrics;

/// <summary>A candidate match returned by a provider's search step.</summary>
public sealed record LyricCandidate(
    LyricSourceKind Source,
    string SourceId,
    string Title,
    string Artist,
    TimeSpan Duration,
    double Score = 0);

/// <summary>
/// A lyric provider: search for candidates, then materialise a document.
/// </summary>
public interface ILyricProvider
{
    LyricSourceKind Kind { get; }

    /// <summary>Human-readable name shown in diagnostics and the UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Providers that need exact identifiers (e.g. QQ Music's <c>songmid</c>)
    /// must search first. Cheap this to call: it is used for scoring.
    /// </summary>
    Task<IReadOnlyList<LyricCandidate>> SearchAsync(
        PlaybackSnapshot track,
        CancellationToken ct);

    /// <summary>Fetch and parse the lyric document for a candidate.</summary>
    Task<LyricDocument> FetchAsync(
        LyricCandidate candidate,
        CancellationToken ct);
}
