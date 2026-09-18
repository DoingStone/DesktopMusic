using System.Text.Json;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Net;

namespace TaskbarLyrics.Core.Lyrics.Providers;

/// <summary>
/// LRCLIB (<see href="https://lrclib.net"/>) provider: an open, no-auth,
/// no-rate-limit-key lyric database. Excellent coverage for Western music and a
/// reliable fallback when the Chinese sources miss.
/// </summary>
public sealed class LrclibProvider : ILyricProvider
{
    private const string Base = "https://lrclib.net/api";
    private const string Referer = "https://lrclib.net/";

    public LyricSourceKind Kind => LyricSourceKind.Lrclib;
    public string DisplayName => "LRCLIB";

    public async Task<IReadOnlyList<LyricCandidate>> SearchAsync(
        PlaybackSnapshot track,
        CancellationToken ct)
    {
        if (!track.HasTrack) return Array.Empty<LyricCandidate>();

        var url = QueryUri.Build(
            $"{Base}/search",
            ("track_name", track.Title),
            ("artist_name", track.Artist));

        var body = await LyricHttp.GetStringAsync(url, Referer, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<LyricCandidate>();

        var results = new List<LyricCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
                if (string.IsNullOrEmpty(id)) continue;

                var name = GetString(item, "trackName") ?? string.Empty;
                var artist = GetString(item, "artistName") ?? string.Empty;
                var duration = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromSeconds(d.GetDouble())
                    : TimeSpan.Zero;

                // Skip entries without timed lyrics -- they cannot drive scrolling.
                var synced = GetString(item, "syncedLyrics");
                if (string.IsNullOrWhiteSpace(synced)) continue;

                results.Add(new LyricCandidate(
                    LyricSourceKind.Lrclib, id, name, artist, duration));
            }
        }
        catch (JsonException)
        {
            return results;
        }

        return results;
    }

    public async Task<LyricDocument> FetchAsync(LyricCandidate candidate, CancellationToken ct)
    {
        // The search response already contains the payload, but re-fetching by id
        // keeps this method self-contained and is cheap. The id is encoded as a
        // path segment so it cannot escape into a different endpoint.
        var url = QueryUri.Build(
            $"{Base}/get/{QueryUri.Encode(candidate.SourceId)}");
        var body = await LyricHttp.GetStringAsync(url, Referer, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return LyricDocument.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var synced = GetString(root, "syncedLyrics");
            if (string.IsNullOrWhiteSpace(synced)) return LyricDocument.Empty;

            var detail = $"LRCLIB · {candidate.Title}";
            return LrcParser.ParseLrc(synced, LyricSourceKind.Lrclib, null, detail);
        }
        catch (JsonException)
        {
            return LyricDocument.Empty;
        }
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
