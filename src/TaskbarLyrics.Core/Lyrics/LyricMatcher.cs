using System.Text;
using System.Text.RegularExpressions;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Lyrics;

/// <summary>
/// Scores a lyric candidate against the track actually playing, so the engine
/// picks the right version (studio vs Live, correct cover) rather than the first
/// search hit.
/// </summary>
public static class LyricMatcher
{
    /// <summary>
    /// Weighting: the title must be right, the artist strongly corroborates, and
    /// duration breaks ties between versions of the same song.
    /// </summary>
    private const double TitleWeight = 55;
    private const double ArtistWeight = 30;
    private const double DurationWeight = 15;

    /// <summary>Duration differences beyond this are treated as a different recording.</summary>
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns 0..100. A score of 80+ means "confident", 90+ means "use
    /// immediately without waiting for other sources".
    /// </summary>
    public static double Score(PlaybackSnapshot track, LyricCandidate candidate)
    {
        if (!track.HasTrack) return 0;

        // Compare titles with the version markers stripped out, so
        // "敢爱敢做 (Live)" is judged on "敢爱敢做". The markers are handled
        // separately as a penalty, not folded into the title similarity.
        var title = Similarity(StripQualifiers(track.Title), StripQualifiers(candidate.Title));
        var artist = ArtistSimilarity(track.Artist, candidate.Artist);
        var duration = DurationSimilarity(track.Duration, candidate.Duration);

        var score = (title * TitleWeight) + (artist * ArtistWeight) + (duration * DurationWeight);

        // A version mismatch only matters when the base title actually matches.
        // Penalising regardless of title similarity used to drag down correct
        // matches whose names merely happened to contain a marker-like substring.
        if (title >= TitleMatchFloor && HasVersionConflict(track.Title, candidate.Title))
        {
            // Scale the penalty with how well the title matches: a strong title
            // match with a differing marker is a genuine wrong-version risk, while
            // a weak match is already being rejected on its own merits.
            score -= VersionPenalty * title;
        }

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>Title similarity below which a version marker is not considered.</summary>
    private const double TitleMatchFloor = 0.7;

    /// <summary>
    /// Maximum score reduction for a differing version marker.
    /// <para>
    /// Sized so a right-title/wrong-version candidate lands in the "plausible but
    /// not confident" band instead of below the acceptance floor: it must lose to
    /// the exact recording, yet stay usable as a fallback when the player only has
    /// the Live/Remix take. A larger penalty previously made those songs display no
    /// lyrics at all.
    /// </para>
    /// </summary>
    private const double VersionPenalty = 12;

    /// <summary>
    /// Version markers, matched only as whole bracketed segments or as a trailing
    /// suffix.
    /// <para>
    /// A plain substring search is wrong here: "live" appears inside "deliver" and
    /// "活着"/"演唱会", and the single character 版 appears inside 出版 and 版本.
    /// Matching those produced phantom "version conflicts" that pushed correct
    /// lyrics below the acceptance threshold.
    /// </para>
    /// </summary>
    private static readonly string[] QualifierWords =
    {
        "live", "remix", "instrumental", "acoustic", "demo", "cover",
        "伴奏", "纯音乐", "现场", "翻唱", "重制", "混音", "合唱",
        "remaster", "remastered", "off vocal", "karaoke",
    };

    /// <summary>
    /// Single characters that are only meaningful as a standalone trailing marker
    /// ("...版"), never as a substring.
    /// </summary>
    private static readonly string[] TrailingMarkers = { "版", "伴奏版" };

    /// <summary>Detect differing version markers between two titles.</summary>
    private static bool HasVersionConflict(string a, string b)
    {
        var qa = Qualifiers(a);
        var qb = Qualifiers(b);

        if (qa.Count == 0 && qb.Count == 0) return false;
        return !qa.SetEquals(qb);
    }

    /// <summary>
    /// Collect version markers from bracketed groups and from an exact trailing
    /// marker, ignoring them anywhere else in the title.
    /// </summary>
    private static HashSet<string> Qualifiers(string s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(s)) return set;

        var text = s.Trim();

        // 1) Bracketed segments: "(Live)", "【伴奏】", "[Remix]".
        foreach (Match match in BracketedSegment.Matches(text))
        {
            var inner = match.Groups["inner"].Value.Trim();
            if (inner.Length == 0) continue;

            foreach (var word in QualifierWords)
            {
                if (ContainsToken(inner, word)) set.Add(word);
            }
        }

        // 2) Exact trailing marker, e.g. "青花瓷 版".
        foreach (var marker in TrailingMarkers)
        {
            if (text.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                var withoutMarker = text[..^marker.Length].TrimEnd(' ', '-', '–', '_');
                if (withoutMarker.Length > 0) set.Add(marker);
            }
        }

        return set;
    }

    private static readonly Regex BracketedSegment = new(
        @"[\(（\[【](?<inner>[^\)）\]】]*)[\)）\]】]",
        RegexOptions.Compiled);

    /// <summary>
    /// Whole-word match for ASCII keywords, substring match for CJK (which has no
    /// word boundaries).
    /// </summary>
    private static bool ContainsToken(string haystack, string needle)
    {
        if (needle.Length == 0) return false;

        var isAscii = needle.All(c => c < 128);
        if (!isAscii)
        {
            return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
            var after = index + needle.Length;
            var afterOk = after >= haystack.Length || !char.IsLetterOrDigit(haystack[after]);

            if (beforeOk && afterOk) return true;
            index++;
        }

        return false;
    }

    /// <summary>Remove every bracketed group, leaving the base title.</summary>
    private static string StripQualifiers(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var stripped = BracketedSegment.Replace(title, " ");

        // Collapse the whitespace left behind so similarity is not skewed.
        return WhitespaceRun.Replace(stripped, " ").Trim();
    }

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    private static double DurationSimilarity(TimeSpan target, TimeSpan candidate)
    {
        // Unknown durations must not punish the candidate.
        if (target <= TimeSpan.Zero || candidate <= TimeSpan.Zero) return 0.5;

        var delta = (target - candidate).Duration();
        if (delta >= DurationTolerance) return 0;

        return 1.0 - (delta.TotalSeconds / DurationTolerance.TotalSeconds);
    }

    /// <summary>
    /// Artist comparison tolerant of multi-artist tags and collab ordering:
    /// "林子祥/叶蒨文" vs "叶蒨文" scores highly because one contains the other.
    /// </summary>
    private static double ArtistSimilarity(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0.5;

        var direct = Similarity(a, b);
        var partsA = SplitArtists(a);
        var partsB = SplitArtists(b);

        double best = direct;
        foreach (var pa in partsA)
        {
            foreach (var pb in partsB)
            {
                best = Math.Max(best, Similarity(pa, pb));
            }
        }

        return best;
    }

    private static string[] SplitArtists(string s) =>
        s.Split(new[] { '/', '、', ',', ';', '&', '＆', '|', '·' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

    /// <summary>
    /// Normalised similarity in 0..1, combining substring containment (good for
    /// CJK titles with suffixes) and edit distance.
    /// </summary>
    public static double Similarity(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);

        if (na.Length == 0 && nb.Length == 0) return 1;
        if (na.Length == 0 || nb.Length == 0) return 0;
        if (na == nb) return 1;

        // Containment: "敢爱敢做" vs "敢爱敢做 (Live)" is still a strong signal,
        // which the qualifier penalty then adjusts.
        var shorter = na.Length <= nb.Length ? na : nb;
        var longer = na.Length <= nb.Length ? nb : na;
        if (longer.Contains(shorter, StringComparison.Ordinal))
        {
            var ratio = (double)shorter.Length / longer.Length;
            return 0.72 + (0.28 * ratio);
        }

        var distance = Levenshtein(na, nb);
        var maxLen = Math.Max(na.Length, nb.Length);
        return 1.0 - ((double)distance / maxLen);
    }

    /// <summary>Strip punctuation, bracketing and spacing so comparisons are stable.</summary>
    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (char.IsPunctuation(ch)) continue;
            if (char.IsSymbol(ch)) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }
}
