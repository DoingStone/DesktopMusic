using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Net;

namespace TaskbarLyrics.Core.Lyrics.Providers;

/// <summary>
/// Word-level lyrics from the AMLL TTML DB: a community library of TTML documents that
/// carry a timestamp for every word.
/// <para>
/// This is the source that makes true real-time highlighting possible. The other
/// providers return LRC, which only says when each <i>line</i> starts, so the renderer has
/// to estimate where inside a line the singing currently is - the highlight then runs
/// ahead on a slow line and stalls on a long note. With per-word timestamps there is
/// nothing to estimate: every word states its own absolute time, so tempo changes, rubato
/// and pauses are all handled without any modelling.
/// </para>
/// <para>
/// Files are named by the identifier of the service they came from, so this provider
/// borrows the existing QQ and NetEase providers to turn a track into identifiers, then
/// looks each one up. A candidate is only accepted when the document's own metadata
/// agrees with the track being played: a search can return a different song with the same
/// title, and word-timed lyrics for the wrong song would be worse than none.
/// </para>
/// </summary>
public sealed class AmllTtmlProvider : ILyricProvider
{
    /// <summary>
    /// Where a document can be fetched from, tried in order. <c>{0}</c> is the database
    /// path, e.g. <c>qq-lyrics/0000Dso20pNy35.ttml</c>.
    /// <para>
    /// The chain exists because the obvious host does not work from this app. Measured
    /// against the real files: <c>cdn.jsdelivr.net</c> serves the document to curl but
    /// returns nothing to <see cref="System.Net.Http.HttpClient"/> - user-agent filtering
    /// or rate limiting - while <c>fastly.jsdelivr.net</c> and the GitHub contents API both
    /// serve it. Without the fallbacks the feature fails silently, because a rejected
    /// request and a song that is simply not in the database look identical from here.
    /// </para>
    /// </summary>
    private static readonly string[] Mirrors =
    {
        // Raw document, no decoding needed.
        "https://fastly.jsdelivr.net/gh/amll-dev/amll-ttml-db@main/{0}",

        // The default host, in case the block above is ever lifted.
        "https://cdn.jsdelivr.net/gh/amll-dev/amll-ttml-db@main/{0}",

        // Contents API: JSON with the document base64-encoded.
        "https://api.github.com/repos/amll-dev/amll-ttml-db/contents/{0}",
    };

    private const string Referer = "https://github.com/amll-dev/amll-ttml-db";

    /// <summary>How many identifiers from each service to look up.</summary>
    private const int ProbePerService = 5;

    private static readonly XNamespace Amll = "http://www.example.com/ns/amll";

    private readonly QqMusicProvider _qq = new();
    private readonly NetEaseProvider _netEase = new();

    /// <summary>
    /// Documents fetched during search, keyed by database path, so the fetch step does not
    /// request the same file twice.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _fetched = new(StringComparer.Ordinal);

    public LyricSourceKind Kind => LyricSourceKind.AmllTtml;

    public string DisplayName => "AMLL 逐字歌词";

    public async Task<IReadOnlyList<LyricCandidate>> SearchAsync(
        PlaybackSnapshot track,
        CancellationToken ct)
    {
        if (!track.HasTrack) return Array.Empty<LyricCandidate>();

        // Ask the services we already integrate with for identifiers, then check whether
        // the word-timed database has a document under any of them.
        var lookups = new List<(LyricSourceKind Origin, string Id, string Title, string Artist, TimeSpan Duration)>();

        lookups.AddRange(await SafeSearchAsync(_qq, track, ct).ConfigureAwait(false));
        lookups.AddRange(await SafeSearchAsync(_netEase, track, ct).ConfigureAwait(false));

        if (lookups.Count == 0) return Array.Empty<LyricCandidate>();

        var probes = lookups
            .GroupBy(l => PathFor(l.Origin, l.Id), StringComparer.Ordinal)
            .Select(g => g.First())
            .Take(ProbePerService * 2)
            .Select(l => ProbeAsync(l, track, ct))
            .ToArray();

        var results = await Task.WhenAll(probes).ConfigureAwait(false);

        return results
            .Where(r => r is not null)
            .Select(r => r!)
            .OrderByDescending(r => r.Score)
            .ToArray();
    }

    public async Task<LyricDocument> FetchAsync(LyricCandidate candidate, CancellationToken ct)
    {
        var path = PathFor(candidate.Source, candidate.SourceId);
        if (path is null) return LyricDocument.Empty;

        var xml = await GetDocumentAsync(path, ct).ConfigureAwait(false);
        if (xml is null) return LyricDocument.Empty;

        return TtmlParser.Parse(xml, LyricSourceKind.AmllTtml, Describe(candidate.Source, candidate.SourceId));
    }

    /// <summary>
    /// Fetch one database entry, then decide whether it really is this track.
    /// Returns null when the file is absent, unusable, or about a different song.
    /// </summary>
    private async Task<LyricCandidate?> ProbeAsync(
        (LyricSourceKind Origin, string Id, string Title, string Artist, TimeSpan Duration) lookup,
        PlaybackSnapshot track,
        CancellationToken ct)
    {
        var path = PathFor(lookup.Origin, lookup.Id);
        if (path is null) return null;

        string? xml;
        try
        {
            xml = await GetDocumentAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A provider must never take the app down; a miss is a miss.
            return null;
        }

        if (string.IsNullOrWhiteSpace(xml)) return null;

        var meta = ReadMetadata(xml);
        if (meta is null) return null;

        double score = Score(meta, track);
        if (score <= 0) return null;

        // Exactly what the resolver needs, and reported so diagnostics show the real match
        // rather than the identifier we happened to search with.
        return new LyricCandidate(
            LyricSourceKind.AmllTtml,
            path,
            meta.Title ?? lookup.Title,
            meta.Artists ?? lookup.Artist,
            meta.Duration ?? lookup.Duration,
            score);
    }

    private static async Task<IReadOnlyList<(LyricSourceKind, string, string, string, TimeSpan)>> SafeSearchAsync(
        ILyricProvider provider,
        PlaybackSnapshot track,
        CancellationToken ct)
    {
        try
        {
            var found = await provider.SearchAsync(track, ct).ConfigureAwait(false);
            return found
                .Take(ProbePerService)
                .Where(c => !string.IsNullOrWhiteSpace(c.SourceId))
                .Select(c => (provider.Kind, c.SourceId, c.Title, c.Artist, c.Duration))
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<(LyricSourceKind, string, string, string, TimeSpan)>();
        }
    }

    /// <summary>
    /// Database location for an identifier, or null for a service the library does not
    /// mirror.
    /// </summary>
    private static string? PathFor(LyricSourceKind origin, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        // Identifiers reach here from search results and from the pinned-source setting, so
        // they may already be a full database path.
        if (id.StartsWith("qq-lyrics/", StringComparison.Ordinal) ||
            id.StartsWith("ncm-lyrics/", StringComparison.Ordinal) ||
            id.StartsWith("am-lyrics/", StringComparison.Ordinal))
        {
            return id.EndsWith(".ttml", StringComparison.OrdinalIgnoreCase) ? id : null;
        }

        // Keep the path inside the database: an identifier is used verbatim in a URL.
        if (id.IndexOfAny(new[] { '/', '\\', '?', '#', ':' }) >= 0) return null;

        return origin switch
        {
            LyricSourceKind.QqMusic => $"qq-lyrics/{id}.ttml",
            LyricSourceKind.NetEase => $"ncm-lyrics/{id}.ttml",
            _ => null,
        };
    }

    private static string Describe(LyricSourceKind origin, string id) =>
        $"{origin} · {id}";

    private async Task<string?> GetDocumentAsync(string path, CancellationToken ct)
    {
        if (_fetched.TryGetValue(path, out var cached)) return cached;

        foreach (var mirror in Mirrors)
        {
            ct.ThrowIfCancellationRequested();

            string? body;
            try
            {
                body = await LyricHttp
                    .GetStringAsync(new Uri(string.Format(mirror, path)), Referer, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A mirror being unreachable is expected; try the next one.
                continue;
            }

            var ttml = ExtractTtml(body);
            if (ttml is null) continue;

            _fetched[path] = ttml;
            return ttml;
        }

        return null;
    }

    /// <summary>
    /// Pull the document out of whatever a mirror returned: the raw XML, or the GitHub
    /// contents API's JSON wrapper. Null when the response is a miss or an error page.
    /// </summary>
    private static string? ExtractTtml(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        // Raw XML.
        if (body.Contains("<tt", StringComparison.OrdinalIgnoreCase)) return body;

        // {"content":"<base64>","encoding":"base64",...}
        if (!body.Contains("\"content\"", StringComparison.Ordinal)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("content", out var content)) return null;

            var decoded = LyricHttp.TryDecodeBase64(content.GetString());
            return decoded is not null && decoded.Contains("<tt", StringComparison.OrdinalIgnoreCase)
                ? decoded
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Metadata the document carries about itself.</summary>
    private sealed record TtmlMeta(string? Title, string? Artists, TimeSpan? Duration);

    private static TtmlMeta? ReadMetadata(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var root = doc.Root;
        if (root is null) return null;

        string? title = null, artists = null;
        foreach (var meta in root.Descendants(Amll + "meta"))
        {
            var key = meta.Attribute("key")?.Value;
            var value = meta.Attribute("value")?.Value;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;

            switch (key)
            {
                case "musicName" when title is null:
                    title = value;
                    break;
                case "artists" when artists is null:
                    artists = value;
                    break;
            }
        }

        TimeSpan? duration = null;
        var body = root.Element(XNamespace.Get("http://www.w3.org/ns/ttml") + "body");
        var dur = TtmlParser.ParseClock(body?.Attribute("dur")?.Value);
        if (dur is not null) duration = dur;

        return new TtmlMeta(title, artists, duration);
    }

    /// <summary>
    /// How well a document matches the track being played. Zero means "reject".
    /// <para>
    /// A search can return several songs sharing a title - live versions, covers,
    /// different artists - and picking the wrong one would highlight the wrong words, which
    /// is worse than falling back to line-level lyrics. The title must agree; the artist
    /// only decides how confident the match is.
    /// </para>
    /// </summary>
    private static double Score(TtmlMeta meta, PlaybackSnapshot track)
    {
        if (string.IsNullOrWhiteSpace(meta.Title)) return 0;

        var dbTitle = Normalize(meta.Title);
        var trackTitle = Normalize(track.Title);
        if (dbTitle.Length == 0 || trackTitle.Length == 0) return 0;

        if (!dbTitle.Contains(trackTitle, StringComparison.Ordinal) &&
            !trackTitle.Contains(dbTitle, StringComparison.Ordinal))
        {
            return 0;
        }

        bool artistAgrees = ArtistsOverlap(meta.Artists, track.Artist);

        // A length disagreement of more than a few seconds usually means a different edit
        // of the song, which would put every timestamp in the wrong place.
        if (meta.Duration is { } dbDur && track.Duration > TimeSpan.Zero &&
            Math.Abs((dbDur - track.Duration).TotalSeconds) > 8)
        {
            return artistAgrees ? 84 : 0;
        }

        // Above the resolver's confident threshold, because word timing strictly beats
        // line timing: when it matches, it should win outright.
        return artistAgrees ? 96 : 80;
    }

    /// <summary>Whether any artist name appears on both sides.</summary>
    private static bool ArtistsOverlap(string? dbArtists, string? trackArtist)
    {
        var a = Tokens(dbArtists);
        var b = Tokens(trackArtist);
        if (a.Count == 0 || b.Count == 0) return false;

        return a.Overlaps(b);
    }

    private static HashSet<string> Tokens(string? value)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value)) return set;

        foreach (var part in value.Split(
                     new[] { '/', '、', ',', ';', '&', '＆', '|', '·', ' ', '\u00a0' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var token = Normalize(part);
            if (token.Length > 0) set.Add(token);
        }

        return set;
    }

    /// <summary>Case- and punctuation-insensitive form used for comparison.</summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormKC))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.LowercaseLetter
                or UnicodeCategory.UppercaseLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber)
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }
}
