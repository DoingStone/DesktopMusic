using System.Globalization;
using System.Text.Json;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Net;

namespace TaskbarLyrics.Core.Lyrics.Providers;

/// <summary>
/// QQ Music lyric provider.
/// <para>
/// Search uses the smartbox endpoint; lyric retrieval uses the <c>musicu</c>
/// <c>GetPlayLyricInfo</c> module. The latter matters because the older
/// <c>fcg_query_lyric_new</c> endpoint returns an <b>empty translation</b> for
/// every song, whereas <c>GetPlayLyricInfo</c> returns both the timed lyric and
/// the timed translation as base64 <b>without requiring a login</b>.
/// </para>
/// </summary>
public sealed class QqMusicProvider : ILyricProvider
{
    private const string Referer = "https://y.qq.com/portal/player.html";

    private const string SearchUrlBase =
        "https://c.y.qq.com/soso/fcgi-bin/client_search_cp";

    private const string MusicuUrl =
        "https://u.y.qq.com/cgi-bin/musicu.fcg";

    public LyricSourceKind Kind => LyricSourceKind.QqMusic;
    public string DisplayName => "QQ音乐";

    public async Task<IReadOnlyList<LyricCandidate>> SearchAsync(
        PlaybackSnapshot track,
        CancellationToken ct)
    {
        if (!track.HasTrack) return Array.Empty<LyricCandidate>();

        // Preferred query: "title artist". Fall back to title alone, which
        // recovers instrumentals and tracks with messy artist tags.
        var queries = new List<string> { $"{track.Title} {track.Artist}".Trim() };
        if (!string.IsNullOrWhiteSpace(track.Title)) queries.Add(track.Title);

        var results = new List<LyricCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var query in queries)
        {
            // Composed through QueryUri so a title containing '&' or '#' cannot
            // corrupt or extend the parameter list.
            Uri url;
            try
            {
                url = QueryUri.Build(
                    SearchUrlBase,
                    ("p", "1"),
                    ("n", "10"),
                    ("w", query),
                    ("format", "json"),
                    ("aggr", "1"),
                    ("cr", "1"),
                    ("lossless", "0"),
                    ("flag_qc", "0"),
                    ("platform", "yqq.json"));
            }
            catch (ArgumentException)
            {
                continue;
            }

            var body = await LyricHttp.GetStringAsync(url, "https://y.qq.com/", ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) continue;

            foreach (var c in ParseSearch(body, seen))
            {
                results.Add(c);
            }

            // Enough candidates to score confidently; stop early to save time.
            if (results.Count >= 8) break;
        }

        return results;
    }

    private static IEnumerable<LyricCandidate> ParseSearch(string body, HashSet<string> seen)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data)) yield break;
            if (!data.TryGetProperty("song", out var song)) yield break;
            if (!song.TryGetProperty("list", out var list)) yield break;
            if (list.ValueKind != JsonValueKind.Array) yield break;

            foreach (var item in list.EnumerateArray())
            {
                var mid = GetString(item, "songmid");
                if (string.IsNullOrEmpty(mid) || !seen.Add(mid)) continue;

                var name = GetString(item, "songname");
                var interval = GetInt(item, "interval");

                var artists = new List<string>();
                if (item.TryGetProperty("singer", out var singers) &&
                    singers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in singers.EnumerateArray())
                    {
                        var n = GetString(s, "name");
                        if (!string.IsNullOrEmpty(n)) artists.Add(n);
                    }
                }

                yield return new LyricCandidate(
                    LyricSourceKind.QqMusic,
                    mid,
                    name ?? string.Empty,
                    string.Join("/", artists),
                    TimeSpan.FromSeconds(interval));
            }
        }
    }

    public async Task<LyricDocument> FetchAsync(LyricCandidate candidate, CancellationToken ct)
    {
        var request = new
        {
            comm = new { ct = 24, cv = 0 },
            req = new
            {
                module = "music.musichallSong.PlayLyricInfo",
                method = "GetPlayLyricInfo",
                param = new
                {
                    songMID = candidate.SourceId,
                    songID = 0,
                    format = "lrc",
                    qrc = 0,
                    trans = 1,
                    roma = 1,
                },
            },
        };

        var json = JsonSerializer.Serialize(request);
        var body = await LyricHttp.PostJsonAsync(new Uri(MusicuUrl), json, Referer, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return LyricDocument.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("req", out var req)) return LyricDocument.Empty;
            if (!req.TryGetProperty("data", out var data)) return LyricDocument.Empty;

            var lyricB64 = GetString(data, "lyric");
            var transB64 = GetString(data, "trans");

            var lyric = LyricHttp.TryDecodeBase64(lyricB64);
            var translation = LyricHttp.TryDecodeBase64(transB64);

            if (string.IsNullOrWhiteSpace(lyric)) return LyricDocument.Empty;

            var detail = $"QQ音乐 · {candidate.Title}";
            return LrcParser.ParseLrc(lyric, LyricSourceKind.QqMusic, translation, detail);
        }
        catch (JsonException)
        {
            return LyricDocument.Empty;
        }
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null,
        };
    }

    private static int GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : 0,
            JsonValueKind.String => int.TryParse(v.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var i) ? i : 0,
            _ => 0,
        };
    }
}
