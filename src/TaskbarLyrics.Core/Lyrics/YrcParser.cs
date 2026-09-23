using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Lyrics;

/// <summary>
/// Parser for NetEase's YRC word-level lyrics.
/// <para>
/// YRC is the format that actually carries per-word timing from NetEase. The older
/// <c>klyric</c> field is usually returned empty, which is why word timing looked
/// unavailable from this service; <c>yrc</c> is only populated when the request asks for it
/// (the <c>yv</c> parameter).
/// </para>
/// <para>Shape, one line per text line:</para>
/// <code>
/// [17630,3810](17630,210,0)字(17840,360,0)字(18200,130,0)字
/// </code>
/// <para>
/// The line tag is a millisecond pair rather than the <c>mm:ss.xx</c> of LRC, and each word
/// carries three numbers where QRC carries two - so the QRC syllable pattern does not match
/// this and a separate parser is needed rather than a tweak to <see cref="LrcParser"/>.
/// </para>
/// </summary>
public static class YrcParser
{
    // [startMs,durationMs] then the words.
    private static readonly Regex LineTag = new(
        @"^\[(?<start>\d+),(?<dur>\d+)\](?<body>.*)$",
        RegexOptions.Compiled);

    // (startMs,durationMs,flag)text - the flag is ignored; it is 0 in every observed file.
    private static readonly Regex Word = new(
        @"\((?<start>\d+),(?<dur>\d+)(?:,-?\d+)?\)(?<text>[^\(]*)",
        RegexOptions.Compiled);

    /// <summary>
    /// Parse a YRC document. Returns an empty document rather than throwing on malformed
    /// input: a bad response from a provider must not take the app down.
    /// </summary>
    public static LyricDocument Parse(
        string yrc,
        LyricSourceKind source = LyricSourceKind.NetEase,
        string? translationLrc = null,
        string? sourceDetail = null)
    {
        if (string.IsNullOrWhiteSpace(yrc)) return LyricDocument.Empty;

        var lines = new List<LyricLine>();
        bool sawWords = false;

        foreach (var raw in yrc.Split('\n'))
        {
            var text = raw.Trim();
            if (text.Length == 0) continue;

            var tag = LineTag.Match(text);
            if (!tag.Success) continue;

            var lineStart = TimeSpan.FromMilliseconds(
                long.Parse(tag.Groups["start"].Value, CultureInfo.InvariantCulture));
            var lineDuration = TimeSpan.FromMilliseconds(
                long.Parse(tag.Groups["dur"].Value, CultureInfo.InvariantCulture));

            var syllables = new List<LyricSyllable>();
            var plain = new StringBuilder();

            foreach (Match w in Word.Matches(tag.Groups["body"].Value))
            {
                var wordText = w.Groups["text"].Value;
                if (wordText.Length == 0) continue;

                // Word times are absolute within the track, as in QRC.
                var start = TimeSpan.FromMilliseconds(
                    long.Parse(w.Groups["start"].Value, CultureInfo.InvariantCulture));
                var duration = TimeSpan.FromMilliseconds(
                    long.Parse(w.Groups["dur"].Value, CultureInfo.InvariantCulture));

                syllables.Add(new LyricSyllable(start, duration, wordText));
                plain.Append(wordText);
            }

            var finalText = plain.Length > 0 ? plain.ToString().Trim() : StripTags(tag.Groups["body"].Value);
            if (finalText.Length == 0) continue;

            if (syllables.Count > 0) sawWords = true;

            var sungEnd = syllables.Count > 0 ? syllables[^1].End : lineStart + lineDuration;
            var lineEnd = lineStart + lineDuration;
            if (sungEnd > lineEnd) lineEnd = sungEnd;

            lines.Add(new LyricLine
            {
                Start = lineStart,
                End = lineEnd,
                Text = finalText,
                IsMetadata = LrcParser.IsMetadataLine(finalText),
                Syllables = syllables,
                SungDuration = syllables.Count > 0 && sungEnd > lineStart
                    ? sungEnd - lineStart
                    : TimeSpan.Zero,
            });
        }

        if (lines.Count == 0) return LyricDocument.Empty;

        if (!string.IsNullOrWhiteSpace(translationLrc))
        {
            var translations = LrcParser.ParseLrc(translationLrc, source).Lines;
            MergeTranslations(lines, translations);
        }

        lines.Sort((a, b) => a.Start.CompareTo(b.Start));

        return new LyricDocument(lines, source, sourceDetail, hasRealWordTiming: sawWords);
    }

    /// <summary>Text of a body that carried no word tags at all.</summary>
    private static string StripTags(string body) =>
        Regex.Replace(body, @"\([^\)]*\)", string.Empty).Trim();

    /// <summary>
    /// Attach translations by matching start times, so a translation that is short a line
    /// or two still lands on the right ones.
    /// </summary>
    private static void MergeTranslations(List<LyricLine> lines, IReadOnlyList<LyricLine> translations)
    {
        if (translations.Count == 0) return;

        var byStart = new Dictionary<long, string>();
        foreach (var t in translations)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) continue;
            byStart[(long)t.Start.TotalMilliseconds] = t.Text;
        }

        foreach (var line in lines)
        {
            // Exact hit first, then a small tolerance: the two files are produced from the
            // same timeline but not always to the same millisecond.
            if (byStart.TryGetValue((long)line.Start.TotalMilliseconds, out var exact))
            {
                line.Translation = exact;
                continue;
            }

            foreach (var (ms, value) in byStart)
            {
                if (Math.Abs(ms - line.Start.TotalMilliseconds) <= 120)
                {
                    line.Translation = value;
                    break;
                }
            }
        }
    }
}
