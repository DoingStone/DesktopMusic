namespace TaskbarLyrics.Core.Models;

/// <summary>Where a lyric document came from.</summary>
public enum LyricSourceKind
{
    None = 0,
    QqMusic,
    /// <summary>Community TTML with per-word timing (AMLL TTML DB).</summary>
    AmllTtml,
    NetEase,
    Kugou,
    Lrclib,
    LocalFile,
    Embedded,
}

/// <summary>
/// A single timed lyric line, optionally carrying per-syllable timing
/// (real QRC or interpolated) for word-by-word highlighting.
/// </summary>
public sealed class LyricLine
{
    /// <summary>Line start time relative to track start.</summary>
    public TimeSpan Start { get; init; }

    /// <summary>
    /// Line end time. Always &gt; <see cref="Start"/>; taken from the next line
    /// start, from explicit QRC duration, or from a sane default.
    /// </summary>
    public TimeSpan End { get; set; }

    /// <summary>Original lyric text (no timestamp).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Translated text for this line, if the source provided one.</summary>
    public string? Translation { get; set; }

    /// <summary>
    /// Per-syllable timing. When populated, drives real word-by-word highlight.
    /// When empty, the renderer interpolates evenly across the line.
    /// </summary>
    public IReadOnlyList<LyricSyllable> Syllables { get; init; } = Array.Empty<LyricSyllable>();

    /// <summary>True when this line holds no singable content (credits, blank, metadata).</summary>
    public bool IsMetadata { get; init; }

    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

    /// <summary>
    /// Estimated time this line is actually <i>sung</i> — what the karaoke progress
    /// must run against.
    /// <para>
    /// <see cref="End"/> is the next line's start, so it spans any instrumental
    /// passage between the two lines. Driving the highlight with that span made it
    /// creep through the instrumental and drift out of step with the singing: the
    /// bug where music time was counted as vocal time. This estimate is always
    /// bounded by that span and can only be shorter.
    /// </para>
    /// </summary>
    public TimeSpan SungDuration { get; set; }

    public override string ToString() => $"[{Start:mm\\:ss\\.ff}] {Text}";
}

/// <summary>A timed fragment inside a line (one or more characters).</summary>
public readonly record struct LyricSyllable(TimeSpan Start, TimeSpan Duration, string Text)
{
    public TimeSpan End => Start + Duration;
}

/// <summary>
/// A fully resolved lyric document ready for display, with a fast
/// position -&gt; line lookup.
/// </summary>
public sealed class LyricDocument
{
    private readonly LyricLine[] _lines;

    public LyricDocument(
        IReadOnlyList<LyricLine> lines,
        LyricSourceKind source,
        string? sourceDetail = null,
        bool hasRealWordTiming = false)
    {
        // Stable sort by start time; the pipeline guarantees lines are distinct.
        _lines = lines.OrderBy(l => l.Start).ToArray();
        Source = source;
        SourceDetail = sourceDetail;
        HasRealWordTiming = hasRealWordTiming;
        FillEndTimes();
    }

    public static LyricDocument Empty { get; } =
        new(Array.Empty<LyricLine>(), LyricSourceKind.None);

    public IReadOnlyList<LyricLine> Lines => _lines;
    public LyricSourceKind Source { get; }
    public string? SourceDetail { get; }
    public bool HasRealWordTiming { get; }
    public bool IsEmpty => _lines.Length == 0;

    /// <summary>End of the last line; useful for "past the end" detection.</summary>
    public TimeSpan TotalDuration => _lines.Length == 0 ? TimeSpan.Zero : _lines[^1].End;

    /// <summary>
    /// Lines that carry actual lyric text. Metadata lines (credits, "作词：", etc.)
    /// still render, but callers can use this to detect instrumental stretches.
    /// </summary>
    public IEnumerable<LyricLine> ContentLines => _lines.Where(l => !l.IsMetadata);

    /// <summary>
    /// Binary-search the line that should be highlighted at <paramref name="position"/>.
    /// Returns -1 before the first line starts or when the document is empty.
    /// </summary>
    public int IndexAt(TimeSpan position)
    {
        if (_lines.Length == 0) return -1;

        // Before the first line begins.
        if (position < _lines[0].Start) return -1;

        int lo = 0, hi = _lines.Length - 1, best = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_lines[mid].Start <= position)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }

    public LyricLine? LineAt(TimeSpan position)
    {
        int i = IndexAt(position);
        return i < 0 ? null : _lines[i];
    }

    /// <summary>
    /// Give every line an end time (the next line's start) and derive the sung
    /// duration the karaoke highlight runs against.
    /// </summary>
    private void FillEndTimes()
    {
        for (int i = 0; i < _lines.Length; i++)
        {
            var line = _lines[i];

            // Word timing is ground truth, so a line that has it keeps its own end.
            //
            // Stretching such a line to the next line's start - which is what the
            // no-timing branch below does, and what this method used to do
            // unconditionally - silently truncates it. Two singers overlap in a duet, so
            // the next line can begin before this one finishes; the truncated tail then
            // holds syllables whose times fall outside the line and the renderer never
            // lights them. Measured on a real TTML file: three syllables lost.
            if (line.Syllables.Count > 0)
            {
                var last = line.Syllables[^1].End;

                // Never end before the singing does, whatever the source claimed.
                if (line.End < last) line.End = last;

                line.SungDuration = last > line.Start ? last - line.Start : line.Duration;
                continue;
            }

            if (i + 1 < _lines.Length)
            {
                var next = _lines[i + 1].Start;
                line.End = next > line.Start ? next : line.Start + Defaults.MinLineDuration;
            }
            else if (line.End <= line.Start)
            {
                line.End = line.Start + Defaults.TailLineDuration;
            }

            // No syllable data: use the full inter-line gap. The previous approach
            // estimated vocal span from a fixed syllable rate (3.5/s), which was
            // wrong for most songs — too slow for pop/rap (highlight lagged behind
            // the voice), too fast for ballads (highlight finished before the line
            // ended). Using the actual LRC gap is the most reliable fallback: the
            // sweep spans exactly the time between this line and the next, driven by
            // the timestamps the lyric file actually provides. An adaptive ease-out
            // curve in the renderer then handles instrumental tails naturally.
            line.SungDuration = line.Duration;
        }
    }

    private static class Defaults
    {
        public static readonly TimeSpan MinLineDuration = TimeSpan.FromMilliseconds(400);
        public static readonly TimeSpan TailLineDuration = TimeSpan.FromSeconds(6);
    }
}
