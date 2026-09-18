using System.Globalization;
using System.Text.RegularExpressions;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Lyrics;

/// <summary>
/// Parses LRC (line-timed) and QRC (word-timed) lyric text.
/// <para>
/// Handles the real-world messiness of Chinese music APIs: multiple timestamps
/// per line, <c>[mm:ss.xx]</c> / <c>[mm:ss.xxx]</c> / <c>[mm:ss:xx]</c> forms,
/// QRC's <c>(start,duration)</c> syllable notation, and translation-only
/// placeholder lines such as <c>//</c>.
/// </para>
/// </summary>
public static class LrcParser
{
    // [00:12.34] or [00:12.345] or [00:12:34]
    private static readonly Regex LineTag = new(
        @"\[(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?\]",
        RegexOptions.Compiled);

    // QRC syllable: (1234,567)text  -- start and duration in milliseconds
    private static readonly Regex Syllable = new(
        @"\((?<start>\d+),(?<dur>\d+)\)(?<text>[^\(\[]*)",
        RegexOptions.Compiled);

    private static readonly Regex LrcTag = new(
        @"^\[(?<k>[a-zA-Z#]+):(?<v>.*)\]$",
        RegexOptions.Compiled);

    /// <summary>Lines whose text is only a translation placeholder.</summary>
    private static readonly HashSet<string> PlaceholderTexts =
        new(StringComparer.Ordinal) { "//", "", "　", "..." };

    /// <summary>
    /// Metadata line prefixes. These are credits, not lyrics, and the renderer
    /// may choose to skip them when hunting for the "current" line.
    /// </summary>
    private static readonly string[] MetadataPrefixes =
    {
        "作词", "作曲", "编曲", "制作人", "词：", "曲：", "编曲：", "制作人：",
        "Lyrics by", "Composed by", "Produced by", "Arranged by", "Written by",
        "混音", "母带", "录音", "监制", "出品", "吉他", "贝斯", "鼓", "键盘",
        "和声", "合声", "配唱", "统筹", "企划", "发行", "OP", "SP", "词曲",
        "弦乐", "人声", "录音师", "混音师", "母带工程师", "封面", "设计",
    };

    /// <summary>
    /// Parse a plain LRC document (line-level timing only).
    /// </summary>
    public static LyricDocument ParseLrc(
        string? lrc,
        LyricSourceKind source,
        string? translationLrc = null,
        string? sourceDetail = null)
    {
        var lines = ParseLrcLines(lrc);
        if (lines.Count == 0) return LyricDocument.Empty;

        var translations = ParseLrcLines(translationLrc);
        if (translations.Count > 0) MergeTranslations(lines, translations);

        PublishDurations(lines);
        OrderInPlace(lines);

        return new LyricDocument(lines, source, sourceDetail, hasRealWordTiming: false);
    }

    /// <summary>
    /// Parse a QRC document, which carries per-syllable timing for precise
    /// word-by-word highlighting. Falls back to LRC semantics when the content
    /// turns out to be plain LRC.
    /// </summary>
    public static LyricDocument ParseQrc(
        string? qrc,
        LyricSourceKind source,
        string? translationLrc = null,
        string? sourceDetail = null)
    {
        if (string.IsNullOrWhiteSpace(qrc)) return LyricDocument.Empty;

        var lines = new List<LyricLine>();
        bool sawSyllables = false;

        foreach (var raw in qrc.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            var text = raw.Trim();
            if (text.Length == 0) continue;

            // Header tags like [ti:...] -- skip, but remember the offset tag.
            if (LrcTag.IsMatch(text) && !LineTag.IsMatch(text)) continue;

            var tag = LineTag.Match(text);
            if (!tag.Success) continue;

            var start = ToTimeSpan(tag);
            var body = text[(tag.Index + tag.Length)..];

            var syllables = new List<LyricSyllable>();
            var plain = new System.Text.StringBuilder();

            foreach (Match sm in Syllable.Matches(body))
            {
                var sylText = sm.Groups["text"].Value;
                if (sylText.Length == 0) continue;

                var sylStart = TimeSpan.FromMilliseconds(long.Parse(sm.Groups["start"].Value, CultureInfo.InvariantCulture));
                var sylDur = TimeSpan.FromMilliseconds(long.Parse(sm.Groups["dur"].Value, CultureInfo.InvariantCulture));

                // QRC timestamps are document-absolute; rebase onto the line tag.
                var absolute = sylStart;
                syllables.Add(new LyricSyllable(absolute, sylDur, sylText));
                plain.Append(sylText);
            }

            string finalText;
            TimeSpan lineEnd = start;

            if (syllables.Count > 0 && plain.Length > 0)
            {
                sawSyllables = true;
                finalText = plain.ToString();
                lineEnd = syllables[^1].End;
            }
            else
            {
                finalText = CleanText(body);
            }

            if (finalText.Length == 0) continue;

            lines.Add(new LyricLine
            {
                Start = start,
                End = lineEnd > start ? lineEnd : start,
                Text = finalText,
                IsMetadata = IsMetadataLine(finalText),
                Syllables = syllables,
            });
        }

        if (lines.Count == 0)
        {
            // Content was actually plain LRC.
            return ParseLrc(qrc, source, translationLrc, sourceDetail);
        }

        var transLines = ParseLrcLines(translationLrc);
        if (transLines.Count > 0) MergeTranslations(lines, transLines);

        PublishDurations(lines);
        OrderInPlace(lines);

        return new LyricDocument(lines, source, sourceDetail, hasRealWordTiming: sawSyllables);
    }

    /// <summary>Parse LRC text into timed lines, preserving duplicates.</summary>
    private static List<LyricLine> ParseLrcLines(string? text)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var offset = TimeSpan.Zero;

        foreach (var raw in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // [offset:+/-ms] shifts the whole document.
            var tagMatch = LrcTag.Match(line);
            if (tagMatch.Success && !LineTag.IsMatch(line))
            {
                var key = tagMatch.Groups["k"].Value.ToLowerInvariant();
                if (key == "offset" &&
                    int.TryParse(tagMatch.Groups["v"].Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                {
                    offset = TimeSpan.FromMilliseconds(ms);
                }
                continue;
            }

            var tags = LineTag.Matches(line);
            if (tags.Count == 0) continue;

            // Everything after the last timestamp is the text.
            var last = tags[^1];
            var body = CleanText(line[(last.Index + last.Length)..]);

            foreach (Match t in tags)
            {
                var start = ToTimeSpan(t) + offset;
                if (start < TimeSpan.Zero) start = TimeSpan.Zero;

                result.Add(new LyricLine
                {
                    Start = start,
                    End = start,
                    Text = body,
                    IsMetadata = IsMetadataLine(body),
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Attach translations to the line with the nearest start time.
    /// Tolerates sources whose translation timestamps drift by a few hundred ms.
    /// </summary>
    private static void MergeTranslations(List<LyricLine> lines, List<LyricLine> translations)
    {
        const int ToleranceMs = 600;

        foreach (var line in lines)
        {
            LyricLine? best = null;
            var bestDelta = TimeSpan.MaxValue;

            foreach (var tr in translations)
            {
                var delta = (tr.Start - line.Start).Duration();
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = tr;
                }
            }

            if (best is null || bestDelta > TimeSpan.FromMilliseconds(ToleranceMs)) continue;

            var text = best.Text.Trim();
            if (text.Length == 0 || PlaceholderTexts.Contains(text)) continue;

            line.Translation = text;
        }
    }

    /// <summary>
    /// Give every line a duration when the source did not supply one, by
    /// clipping each line to the next line's start.
    /// </summary>
    private static void PublishDurations(List<LyricLine> lines)
    {
        var ordered = lines.OrderBy(l => l.Start).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            var line = ordered[i];
            if (line.End > line.Start) continue;
            if (i + 1 < ordered.Length) line.End = ordered[i + 1].Start;
        }
    }

    private static void OrderInPlace(List<LyricLine> lines) =>
        lines.Sort((a, b) => a.Start.CompareTo(b.Start));

    private static TimeSpan ToTimeSpan(Match tag)
    {
        var m = int.Parse(tag.Groups["m"].Value, CultureInfo.InvariantCulture);
        var s = int.Parse(tag.Groups["s"].Value, CultureInfo.InvariantCulture);

        double fraction = 0;
        if (tag.Groups["f"].Success)
        {
            var digits = tag.Groups["f"].Value;
            fraction = int.Parse(digits, CultureInfo.InvariantCulture) / Math.Pow(10, digits.Length);
        }

        return TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s) + TimeSpan.FromSeconds(fraction);
    }

    private static string CleanText(string s) =>
        s.Replace("\u200b", string.Empty).Trim();

    /// <summary>Heuristic: is this a credit/metadata line rather than a lyric?</summary>
    public static bool IsMetadataLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        var t = text.Trim();

        // Pure separators / instrumental markers.
        if (t is "//" or "..." or "……") return true;

        foreach (var prefix in MetadataPrefixes)
        {
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
