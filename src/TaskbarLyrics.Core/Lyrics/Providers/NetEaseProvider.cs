using System.Globalization;
using System.Text.Json;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Net;

namespace TaskbarLyrics.Core.Lyrics.Providers;

/// <summary>
/// NetEase Cloud Music (网易云音乐) provider. Complements QQ Music because
/// NetEase's catalogue and translation coverage differ; when one source has a
/// poor match the other usually succeeds.
/// </summary>
public sealed class NetEaseProvider : ILyricProvider
{
    private const string Referer = "https://music.163.com/";
    private const string SearchBase = "https://music.163.com/api/search/get";
    private const string LyricBase = "https://music.163.com/api/song/lyric";

    /// <summary>Song metadata by id, used when the player published its own identifier.</summary>
    private const string DetailBase = "https://music.163.com/api/song/detail";

    private const string Headers =
        "&lv=-1&kv=-1&tv=-1";

    public LyricSourceKind Kind => LyricSourceKind.NetEase;
    public string DisplayName => "网易云音乐";

    public async Task<IReadOnlyList<LyricCandidate>> SearchAsync(
        PlaybackSnapshot track,
        CancellationToken ct)
    {
        if (!track.HasTrack) return Array.Empty<LyricCandidate>();

        // When the player published its own song id - the InfLink-rs plugin does, as
        // "NCM-{id}" in the genre field - fetch that exact song rather than searching. A
        // search can land on a cover, a live take or a different edit whose timings do not
        // fit the recording being heard; the id cannot.
        if (!string.IsNullOrWhiteSpace(track.ExactSourceId))
        {
            var exact = await LookupByIdAsync(track.ExactSourceId, ct).ConfigureAwait(false);
            if (exact is not null) return new[] { exact };
        }

        var query = $"{track.Title} {track.Artist}".Trim();
        var url = QueryUri.Build(SearchBase, ("s", query), ("type", "1"), ("offset", "0"), ("limit", "10"));

        var body = await LyricHttp.GetStringAsync(url, Referer, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<LyricCandidate>();

        var results = new List<LyricCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("result", out var result)) return results;
            if (!result.TryGetProperty("songs", out var songs)) return results;
            if (songs.ValueKind != JsonValueKind.Array) return results;

            foreach (var item in songs.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
                if (string.IsNullOrEmpty(id)) continue;

                var name = GetString(item, "name") ?? string.Empty;

                var artists = new List<string>();
                if (item.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in arr.EnumerateArray())
                    {
                        var n = GetString(a, "name");
                        if (!string.IsNullOrEmpty(n)) artists.Add(n);
                    }
                }

                var duration = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromMilliseconds(d.GetDouble())
                    : TimeSpan.Zero;

                results.Add(new LyricCandidate(
                    LyricSourceKind.NetEase, id, name, string.Join("/", artists), duration));
            }
        }
        catch (JsonException)
        {
            return results;
        }

        return results;
    }

    /// <summary>
    /// Build a candidate straight from a song id, so the exact recording is used with no
    /// title matching involved. Returns null when the id is unknown to the service, in which
    /// case the caller falls back to searching.
    /// </summary>
    private async Task<LyricCandidate?> LookupByIdAsync(string id, CancellationToken ct)
    {
        var url = QueryUri.Build(DetailBase, ("ids", $"[{id}]"));

        var body = await LyricHttp.GetStringAsync(url, Referer, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("songs", out var songs)) return null;
            if (songs.ValueKind != JsonValueKind.Array || songs.GetArrayLength() == 0) return null;

            var song = songs[0];
            var name = GetString(song, "name") ?? string.Empty;
            if (name.Length == 0) return null;

            var artists = new List<string>();
            if (song.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var artistName = GetString(a, "name");
                    if (!string.IsNullOrWhiteSpace(artistName)) artists.Add(artistName);
                }
            }

            var duration = song.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                ? TimeSpan.FromMilliseconds(d.GetDouble())
                : TimeSpan.Zero;

            return new LyricCandidate(
                LyricSourceKind.NetEase, id, name, string.Join("/", artists), duration);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<LyricDocument> FetchAsync(LyricCandidate candidate, CancellationToken ct)
    {
        // yv asks for yrc, the word-level track. It is absent from the response entirely
        // unless requested, which is why word timing looked unavailable from NetEase: the
        // older klyric field - the one that is usually empty - is not the word-level source.
        var url = QueryUri.Build(
            LyricBase,
            ("id", candidate.SourceId),
            ("lv", "-1"),
            ("kv", "-1"),
            ("tv", "-1"),
            ("yv", "-1"),
            ("rv", "-1"));

        var body = await LyricHttp.GetStringAsync(url, Referer, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return LyricDocument.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string? lyric = null;
            if (root.TryGetProperty("lrc", out var lrc))
                lyric = GetString(lrc, "lyric");

            string? wordTimed = null;
            if (root.TryGetProperty("yrc", out var yrc))
                wordTimed = GetString(yrc, "lyric");

            string? translation = null;
            if (root.TryGetProperty("tlyric", out var tlyric))
                translation = GetString(tlyric, "lyric");

            var detail = $"网易云音乐 · {candidate.Title}";

            // Word timing first: it strictly beats line timing for the highlight. Fall back
            // to the plain lyric when the song has no yrc, which is common for older
            // catalogue entries.
            if (!string.IsNullOrWhiteSpace(wordTimed))
            {
                var wordDoc = YrcParser.Parse(wordTimed, LyricSourceKind.NetEase, translation, detail);
                if (wordDoc.HasRealWordTiming) return wordDoc;
            }

            if (string.IsNullOrWhiteSpace(lyric)) return LyricDocument.Empty;

            return LrcParser.ParseLrc(lyric, LyricSourceKind.NetEase, translation, detail);
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
