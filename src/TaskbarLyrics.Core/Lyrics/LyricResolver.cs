using System.Collections.Concurrent;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Lyrics;

/// <summary>Outcome of a resolution attempt, including which source won and why.</summary>
public sealed record LyricResolution(
    LyricDocument Document,
    LyricSourceKind Source,
    LyricCandidate? Winner,
    double Score,
    IReadOnlyList<ProviderTrace> Traces)
{
    public bool Found => !Document.IsEmpty;

    public static LyricResolution NotFound(IReadOnlyList<ProviderTrace> traces) =>
        new(LyricDocument.Empty, LyricSourceKind.None, null, 0, traces);
}

/// <summary>Per-provider diagnostic record, surfaced in the UI's match page.</summary>
public sealed record ProviderTrace(
    LyricSourceKind Source,
    string DisplayName,
    int CandidatesFound,
    double BestScore,
    string? BestTitle,
    string? BestArtist,
    string Status);

/// <summary>
/// Resolves the lyric document for a track by querying every enabled provider
/// concurrently, scoring candidates, and choosing the best.
/// </summary>
public sealed class LyricResolver
{
    /// <summary>At or above this score a source wins outright, no need to wait for the rest.</summary>
    public const double ConfidentScore = 90;

    /// <summary>Below this score a candidate is never accepted.</summary>
    public const double MinimumScore = 78;

    private readonly IReadOnlyList<ILyricProvider> _providers;
    private readonly ConcurrentDictionary<string, LyricResolution> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LyricCandidate> _pinned = new(StringComparer.Ordinal);

    public LyricResolver(IEnumerable<ILyricProvider> providers)
    {
        _providers = providers.ToArray();
    }

    public IReadOnlyList<ILyricProvider> Providers => _providers;

    /// <summary>
    /// Resolve lyrics for <paramref name="track"/>. Results are cached by track
    /// identity, so repeated polls cost nothing.
    /// </summary>
    public async Task<LyricResolution> ResolveAsync(PlaybackSnapshot track, CancellationToken ct)
    {
        if (!track.HasTrack) return LyricResolution.NotFound(Array.Empty<ProviderTrace>());

        var key = track.CacheKey;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        // A pinned choice (user picked this exact candidate) always wins.
        if (_pinned.TryGetValue(key, out var pinned))
        {
            foreach (var p in _providers.Where(p => p.Kind == pinned.Source))
            {
                var doc = await p.FetchAsync(pinned, ct).ConfigureAwait(false);
                if (!doc.IsEmpty)
                {
                    var pinnedResolution = new LyricResolution(doc, pinned.Source, pinned, 100,
                        Array.Empty<ProviderTrace>());
                    _cache[key] = pinnedResolution;
                    return pinnedResolution;
                }
            }
        }

        var traces = new ConcurrentBag<ProviderTrace>();

        // Run every provider concurrently; one slow source must not stall the rest.
        var tasks = _providers.Select(p => SearchOneAsync(p, track, traces, ct)).ToArray();
        var scored = (await Task.WhenAll(tasks).ConfigureAwait(false))
            .SelectMany(x => x)
            .OrderByDescending(x => x.Score)
            .ToArray();

        // Try candidates in descending confidence until one yields real lyrics.
        foreach (var best in scored)
        {
            if (best.Score < MinimumScore) break;

            var provider = _providers.FirstOrDefault(p => p.Kind == best.Candidate.Source);
            if (provider is null) continue;

            var doc = await provider.FetchAsync(best.Candidate, ct).ConfigureAwait(false);
            if (doc.IsEmpty) continue;

            var resolution = new LyricResolution(
                doc, best.Candidate.Source, best.Candidate, best.Score, traces.ToArray());

            _cache[key] = resolution;
            return resolution;
        }

        var notFound = LyricResolution.NotFound(traces.ToArray());
        _cache[key] = notFound;
        return notFound;
    }

    private static async Task<IReadOnlyList<Scored>> SearchOneAsync(
        ILyricProvider provider,
        PlaybackSnapshot track,
        ConcurrentBag<ProviderTrace> traces,
        CancellationToken ct)
    {
        try
        {
            var candidates = await provider.SearchAsync(track, ct).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                traces.Add(new ProviderTrace(provider.Kind, provider.DisplayName, 0, 0,
                    null, null, "无结果"));
                return Array.Empty<Scored>();
            }

            var scored = candidates
                .Select(c => new Scored(c, LyricMatcher.Score(track, c)))
                .OrderByDescending(s => s.Score)
                .ToArray();

            var top = scored[0];
            traces.Add(new ProviderTrace(
                provider.Kind, provider.DisplayName, candidates.Count, top.Score,
                top.Candidate.Title, top.Candidate.Artist, "有结果"));

            return scored;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            traces.Add(new ProviderTrace(provider.Kind, provider.DisplayName, 0, 0,
                null, null, "错误: " + ex.Message));
            return Array.Empty<Scored>();
        }
    }

    /// <summary>Force a specific candidate for this track and remember the choice.</summary>
    public void Pin(PlaybackSnapshot track, LyricCandidate candidate)
    {
        if (!track.HasTrack) return;
        var key = track.CacheKey;
        _pinned[key] = candidate;
        _cache.TryRemove(key, out _);
    }

    /// <summary>Drop all caching so the next resolve re-queries the network.</summary>
    public void ClearCache()
    {
        _cache.Clear();
        _pinned.Clear();
    }

    private readonly record struct Scored(LyricCandidate Candidate, double Score);
}
