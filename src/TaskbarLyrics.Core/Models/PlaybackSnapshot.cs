namespace TaskbarLyrics.Core.Models;

/// <summary>
/// A media session snapshot: identity plus the playback position extrapolated
/// to "now" so the lyric renderer never lags behind.
/// </summary>
public sealed record PlaybackSnapshot(
    string SessionId,
    string SourceAppId,
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    TimeSpan Position,
    bool IsPlaying,
    DateTimeOffset CapturedAt)
{
    /// <summary>
    /// When the player last updated <see cref="Position"/>, as reported by the OS
    /// media session (<c>TimelineProperties.LastUpdatedTime</c>).
    /// <para>
    /// This is the correct anchor for interpolation. <see cref="CapturedAt"/> only
    /// says when <i>we</i> polled, so a player that reports its timeline lazily
    /// would leave us counting from an already-stale position and the lyrics would
    /// lag by however old that sample was. Default means "unknown"; interpolation
    /// then falls back to <see cref="CapturedAt"/>.
    /// </para>
    /// </summary>
    public DateTimeOffset PositionUpdatedAt { get; init; }

    /// <summary>
    /// Player speed multiplier (1.0 = normal). Ignoring it makes interpolated
    /// position advance at wall-clock rate even during speed-changed playback, which
    /// drifts further out of sync the longer the track runs.
    /// </summary>
    public double PlaybackRate { get; init; } = 1.0;

    /// <summary>
    /// The service's own identifier for this track, when the player publishes one.
    /// <para>
    /// NetEase Cloud Music exposes no id through the media session, but the InfLink-rs
    /// plugin smuggles it through the genre field as <c>NCM-{id}</c>. Having the id means the
    /// exact song's lyrics can be fetched directly rather than searched for, which removes a
    /// whole class of wrong matches: a title-and-artist search can land on a cover, a live
    /// take or a different edit, and none of those timings fit the recording being heard.
    /// </para>
    /// <para>Null when the player publishes nothing usable.</para>
    /// </summary>
    public string? ExactSourceId { get; init; }

    public static PlaybackSnapshot Empty { get; } = new(
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        TimeSpan.Zero, TimeSpan.Zero, false, DateTimeOffset.MinValue);

    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);

    /// <summary>
    /// Identity used for lyric lookup and caching. Artist order is normalised so
    /// "A/B" and "B/A" resolve to the same cache entry.
    /// </summary>
    public string CacheKey =>
        $"{SourceAppId}|{Normalize(Title)}|{NormalizeArtists(Artist)}";

    /// <summary>
    /// Position advanced by the time elapsed since the player last reported it, but
    /// only while playing. This is what makes the highlight scroll smoothly between
    /// the roughly once-per-second SMTC updates.
    /// </summary>
    public TimeSpan ExtrapolatedPosition(DateTimeOffset now)
    {
        if (!IsPlaying) return Position;

        // Anchor to the player's own timestamp when it gave one: that measures from
        // the moment the position was actually sampled, not from when we polled.
        var anchor = PositionUpdatedAt == default ? CapturedAt : PositionUpdatedAt;

        var elapsed = now - anchor;
        if (elapsed <= TimeSpan.Zero) return Position;

        // Scale by the reported rate so a speed-changed player does not drift.
        var rate = PlaybackRate > 0.01 && Math.Abs(PlaybackRate - 1.0) < 4.0 ? PlaybackRate : 1.0;

        var pos = Position + TimeSpan.FromTicks((long)(elapsed.Ticks * rate));
        if (Duration > TimeSpan.Zero && pos > Duration) return Duration;

        return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
    }

    private static string Normalize(string? s) =>
        string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim().ToLowerInvariant();

    /// <summary>
    /// Splits the artist string on common separators and sorts the parts, so
    /// "林子祥/叶蒨文" and "叶蒨文/林子祥" produce the same key.
    /// </summary>
    private static string NormalizeArtists(string? artists)
    {
        if (string.IsNullOrWhiteSpace(artists)) return string.Empty;

        var parts = artists
            .Split(new[] { '/', '、', ',', ';', '&', '＆', '|', '·' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().ToLowerInvariant())
            .Where(p => p.Length > 0)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        return parts.Length == 0 ? string.Empty : string.Join("/", parts);
    }
}
