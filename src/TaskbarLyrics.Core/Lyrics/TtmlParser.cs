using System.Globalization;
using System.Xml.Linq;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Lyrics;

/// <summary>
/// Parser for TTML lyric documents with word-level timing, as published by the
/// AMLL TTML DB and by Apple Music.
/// <para>
/// This is the source that makes true real-time highlighting possible. An LRC file
/// only says when each <i>line</i> starts, so the renderer has to guess where inside
/// a line the singing currently is - which is why a slow line runs ahead and a long
/// held note stalls. TTML carries a timestamp per word, so tempo changes, rubato and
/// pauses need no modelling at all: every word states its own absolute time.
/// </para>
/// </summary>
public static class TtmlParser
{
    private static readonly XNamespace Tt = "http://www.w3.org/ns/ttml";
    private static readonly XNamespace Ttm = "http://www.w3.org/ns/ttml#metadata";

    /// <summary>Role marking a background-vocal layer, which is not the main line.</summary>
    private const string BackgroundRole = "x-bg";

    /// <summary>
    /// Parse a TTML document. Returns an empty document rather than throwing when the
    /// XML is malformed: a bad file from a provider must not take the app down.
    /// </summary>
    public static LyricDocument Parse(
        string ttml,
        LyricSourceKind source = LyricSourceKind.AmllTtml,
        string? sourceDetail = null)
    {
        if (string.IsNullOrWhiteSpace(ttml)) return LyricDocument.Empty;

        XDocument xml;
        try
        {
            xml = XDocument.Parse(ttml, LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            return LyricDocument.Empty;
        }

        var body = xml.Root?.Element(Tt + "body");
        if (body is null) return LyricDocument.Empty;

        var lines = new List<LyricLine>();
        bool sawWords = false;

        // Paragraphs may sit directly under body, or inside one or more divisions.
        foreach (var p in body.Descendants(Tt + "p"))
        {
            var line = ParseParagraph(p, ref sawWords);
            if (line is not null) lines.Add(line);
        }

        if (lines.Count == 0) return LyricDocument.Empty;

        lines.Sort((a, b) => a.Start.CompareTo(b.Start));
        FillMissingEnds(lines);

        return new LyricDocument(lines, source, sourceDetail, hasRealWordTiming: sawWords);
    }

    private static LyricLine? ParseParagraph(XElement p, ref bool sawWords)
    {
        var start = ParseClock(p.Attribute("begin")?.Value);
        if (start is null) return null;

        var end = ParseClock(p.Attribute("end")?.Value);

        var syllables = new List<LyricSyllable>();
        var text = new System.Text.StringBuilder();
        string? translation = null;

        foreach (var span in p.Elements(Tt + "span"))
        {
            var role = span.Attribute(Ttm + "role")?.Value;

            // Background vocals are a separate layer sung underneath the lead. Folding
            // them into the line would double its text and desynchronise the sweep.
            if (string.Equals(role, BackgroundRole, StringComparison.OrdinalIgnoreCase)) continue;

            // A translation is carried as another span layer, not as a lyric.
            if (role is not null && role.Contains("translation", StringComparison.OrdinalIgnoreCase))
            {
                translation = Collapse(span.Value);
                continue;
            }

            var spanStart = ParseClock(span.Attribute("begin")?.Value);
            var spanEnd = ParseClock(span.Attribute("end")?.Value);
            var spanText = span.Value;

            if (spanText.Length == 0) continue;

            if (spanStart is not null && spanEnd is not null && spanEnd > spanStart)
            {
                syllables.Add(new LyricSyllable(spanStart.Value, spanEnd.Value - spanStart.Value, spanText));
                sawWords = true;
            }

            text.Append(spanText);
        }

        // A line whose spans carried no timing still has usable text.
        var lineText = Collapse(text.Length > 0 ? text.ToString() : p.Value);
        if (lineText.Length == 0) return null;

        var sungDuration = syllables.Count > 0
            ? syllables[^1].End - start.Value
            : TimeSpan.Zero;

        return new LyricLine
        {
            Start = start.Value,
            End = end ?? (syllables.Count > 0 ? syllables[^1].End : start.Value),
            Text = lineText,
            Translation = translation,
            IsMetadata = LrcParser.IsMetadataLine(lineText),
            Syllables = syllables,
            SungDuration = sungDuration > TimeSpan.Zero ? sungDuration : TimeSpan.Zero,
        };
    }

    /// <summary>Any line still without an end borrows the next line's start.</summary>
    private static void FillMissingEnds(List<LyricLine> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.End > line.Start) continue;
            if (i + 1 < lines.Count) line.End = lines[i + 1].Start;
            else line.End = line.Start + TimeSpan.FromSeconds(4);
        }
    }

    /// <summary>
    /// TTML clock values: <c>hh:mm:ss.mmm</c>, <c>hh:mm:ss:ff</c> (frames, assumed 30 fps),
    /// or an offset such as <c>1250ms</c> / <c>12.5s</c> / <c>1.5m</c>.
    /// </summary>
    public static TimeSpan? ParseClock(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        value = value.Trim();

        // Two-character unit first: "1250ms" would otherwise be read as the unit "s" with
        // a leftover "m" in the number.
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            return double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
                ? TimeSpan.FromMilliseconds(ms)
                : null;
        }

        // Offset form: a number followed by a unit.
        if (char.IsDigit(value[^1]))
        {
            // Pure seconds with no unit and no colon.
            if (!value.Contains(':'))
            {
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)
                    ? TimeSpan.FromSeconds(secs)
                    : null;
            }
        }
        else
        {
            var unit = char.ToLowerInvariant(value[^1]);
            var numberPart = value[..^1];
            if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return null;

            return unit switch
            {
                'h' => TimeSpan.FromHours(n),
                'm' => TimeSpan.FromMinutes(n),
                's' => TimeSpan.FromSeconds(n),
                'f' => TimeSpan.FromSeconds(n / 30.0),
                't' => TimeSpan.FromSeconds(n / 10000.0),
                _ => null,
            };
        }

        // Clock form: hh:mm:ss(.fff | :ff)
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 4) return null;

        if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var last))
            return null;

        double total = last;
        if (parts.Length >= 2)
        {
            if (!int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
                return null;
            total += minutes * 60;
        }
        if (parts.Length >= 3)
        {
            if (!int.TryParse(parts[^3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours))
                return null;
            total += hours * 3600;
        }

        return TimeSpan.FromSeconds(total);
    }

    /// <summary>Trim and fold internal newlines, which TTML uses for source formatting.</summary>
    private static string Collapse(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}
