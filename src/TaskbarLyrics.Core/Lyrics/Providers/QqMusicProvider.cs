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
        // QRC would carry per-word timing, which is what lets the highlight follow a
        // singer who rushes or drags. It is opt-in because the payload this endpoint
        // returns does not open with the published .qrc scheme: our 3DES port reproduces
        // the reference implementation byte-for-byte, and the reference itself fails on
        // this payload too (see QrcDecryptor and tools/qrc-*.py). Until the container is
        // identified the attempt costs an extra request and cannot succeed, so it is off
        // by default.
        if (WordTimingEnabled)
        {
            var qrc = await TryFetchQrcAsync(candidate, ct).ConfigureAwait(false);
            if (qrc is not null) return qrc;
        }

        return await FetchLrcAsync(candidate, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether to attempt the word-timed QRC fetch. Enabled with <c>TBL_QQ_QRC=1</c>,
    /// so the default path costs one request per song instead of two.
    /// </summary>
    internal static bool WordTimingEnabled =>
        Environment.GetEnvironmentVariable("TBL_QQ_QRC") == "1";

    /// <summary>
    /// Fetch and decrypt word-timed QRC, or null when the server has none / the format
    /// changed. Plain LRC still carries the translation, so it is fetched alongside.
    /// </summary>
    private async Task<LyricDocument?> TryFetchQrcAsync(LyricCandidate candidate, CancellationToken ct)
    {
        try
        {
            var (qrcText, transText) = await RequestAsync(candidate, "qrc", qrc: 1, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(qrcText)) return null;

            // The field is base64 over the raw ciphertext.
            var cipher = Convert.FromBase64String(qrcText);
            var plain = QrcDecryptor.TryRead(cipher);
            if (string.IsNullOrWhiteSpace(plain)) return null;

            var detail = $"QQ音乐(逐字) · {candidate.Title}";
            return LrcParser.ParseLrc(plain, LyricSourceKind.QqMusic, transText, detail);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private async Task<LyricDocument> FetchLrcAsync(LyricCandidate candidate, CancellationToken ct)
    {
        var (lyric, translation) = await RequestAsync(candidate, "lrc", qrc: 0, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(lyric)) return LyricDocument.Empty;

        var detail = $"QQ音乐 · {candidate.Title}";
        return LrcParser.ParseLrc(lyric, LyricSourceKind.QqMusic, translation, detail);
    }

    /// <summary>
    /// Call GetPlayLyricInfo and return the decoded lyric and translation text.
    /// </summary>
    private static async Task<(string? Lyric, string? Translation)> RequestAsync(
        LyricCandidate candidate, string format, int qrc, CancellationToken ct)
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
                    format,
                    qrc,
                    trans = 1,
                    roma = 1,

                    // Required. Without crypt=1 the server returns the QRC payload in a
                    // form the 3DES routine cannot open — decryption yields noise and
                    // inflate fails. The _t flags and type mirror the reference client.
                    crypt = 0,
                    lrc_t = 0,
                    qrc_t = 0,
                    roma_t = 0,
                    trans_t = 0,
                    type = 1,
                },
            },
        };

        var json = JsonSerializer.Serialize(request);
        var body = await LyricHttp.PostJsonAsync(new Uri(MusicuUrl), json, Referer, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return (null, null);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("req", out var req)) return (null, null);
        if (!req.TryGetProperty("data", out var data)) return (null, null);

        var lyricRaw = GetString(data, "lyric");
        var transRaw = GetString(data, "trans");

        // QRC comes back as base64 ciphertext and is decoded by the caller; LRC comes
        // back as base64 text.
        if (qrc == 1) return (lyricRaw, LyricHttp.TryDecodeBase64(transRaw));

        return (LyricHttp.TryDecodeBase64(lyricRaw), LyricHttp.TryDecodeBase64(transRaw));
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
