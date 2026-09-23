namespace TaskbarLyrics.Core.Models;

/// <summary>
/// A self-correcting playback clock for the lyric renderer.
/// <para>
/// SMTC reports a position roughly once per second, so the renderer has to
/// interpolate in between. Naive interpolation drifts for two reasons:
/// <see cref="PlaybackSnapshot.PlaybackRate"/> is frequently 1.0 (or absent) even
/// when the user changed playback speed, and a freshly reported position rarely
/// lands exactly on the interpolation — snapping to it is a visible stutter.
/// </para>
/// <para>
/// This clock addresses both. It measures the real rate from two consecutive
/// <i>distinct</i> samples, preferring that measurement over the reported rate, and
/// it absorbs each correction over a short window so the highlight slides to the
/// truth instead of jumping. Only a difference far too large to be drift (a seek or
/// a track change) is applied immediately.
/// </para>
/// <para>
/// Stateful and thread-safe: the renderer reads it from a timer while the media
/// watcher feeds it snapshots. Keep it to one instance per playback session and
/// <see cref="Reset"/> it on a track or session change.
/// </para>
/// </summary>
public sealed class PlaybackClock
{
    /// <summary>
    /// How long a reported-vs-predicted difference takes to be absorbed. Long
    /// enough to be invisible at frame rate, short enough to finish well before
    /// the next SMTC sample arrives.
    /// </summary>
    private static readonly TimeSpan CorrectionWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// A difference this large is not drift: it is a seek, a track change or a
    /// resume at a new offset. Creeping towards it would leave the highlight
    /// scrolling through the wrong verse, so snap instead.
    /// </summary>
    private static readonly TimeSpan SnapThreshold = TimeSpan.FromSeconds(1.75);

    /// <summary>
    /// Shortest anchor-to-anchor interval a rate may be measured over. Over a few
    /// tens of milliseconds, the 100 ns position quantisation of a player becomes a
    /// wildly wrong rate.
    /// </summary>
    private static readonly TimeSpan MinRateInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Plausible playback speeds; anything outside is a bad measurement.</summary>
    private const double MinRate = 0.25;
    private const double MaxRate = 4.0;

    /// <summary>
    /// Cap on how far a single extrapolation may advance, so an unknown anchor
    /// (default timestamps) cannot overflow the arithmetic.
    /// </summary>
    private static readonly long MaxAdvanceTicks = TimeSpan.FromDays(1).Ticks;

    /// <summary>Guards every field below; <see cref="Position"/> is called per frame.</summary>
    private readonly object _gate = new();

    // What we currently believe: _basePosition was true at _baseAt, advancing at _rate.
    private TimeSpan _basePosition;
    private DateTimeOffset _baseAt;
    private double _rate = 1.0;
    private bool _isPlaying;

    // Correction in flight. An offset in position units that decays to zero over
    // CorrectionWindow starting at _correctionAt, layered on top of the
    // extrapolation. Applying it this way means the rendered position is continuous
    // at the instant a correction is absorbed, then slides onto the reported value.
    private TimeSpan _offset;
    private DateTimeOffset _correctionAt;

    // The last distinct sample, and the rate measured from the one before it.
    private bool _hasSample;
    private TimeSpan _samplePosition;
    private DateTimeOffset _sampleAt;
    private bool _sampleWasPlaying;
    private double? _derivedRate;

    /// <summary>Upper bound for the returned position, when the player reports one.</summary>
    private TimeSpan _duration;

    /// <summary>
    /// Feed every polled snapshot, along with the moment it was polled.
    /// <para>
    /// Calling this repeatedly with the same underlying sample is both expected and
    /// harmless: a sample only counts as new information when its position or its
    /// <see cref="PlaybackSnapshot.PositionUpdatedAt"/> anchor differs from the last
    /// one seen.
    /// </para>
    /// </summary>
    public void Observe(PlaybackSnapshot snapshot, DateTimeOffset now)
    {
        lock (_gate)
        {
            // LastUpdatedTime is the moment the player sampled the position. Poll time
            // is only a fallback, for players that never report a timeline timestamp.
            var sampleAt = snapshot.PositionUpdatedAt == default
                ? snapshot.CapturedAt
                : snapshot.PositionUpdatedAt;

            // Both the anchor and the position have to match for this to be the same
            // sample we already have. Two thirds of polls land here.
            var distinct = !_hasSample
                           || sampleAt != _sampleAt
                           || snapshot.Position != _samplePosition;

            var playStateChanged = snapshot.IsPlaying != _isPlaying;
            if (!distinct && !playStateChanged) return;

            // Keep a known bound rather than letting a transient zero clear it; the
            // clock is reset on a track change, so this cannot leak across tracks.
            if (snapshot.Duration > TimeSpan.Zero) _duration = snapshot.Duration;

            // What the renderer is showing at this instant, what we believed at the
            // moment the player took its sample, and whether we believed anything yet.
            var reference = Predict(now);
            var predictedAtSample = distinct ? Predict(sampleAt) : reference;
            var hadBelief = _hasSample;

            if (playStateChanged)
            {
                // A rate measured before a pause does not describe playback after it.
                _derivedRate = null;
            }

            if (distinct)
            {
                // Measure the real rate from the two most recent distinct samples,
                // anchored to the player's own timestamps so a lazy poller cannot
                // distort it. Only same-state (both playing or both paused) samples
                // may be paired: the gap across a pause is not elapsed playback.
                if (hadBelief && _sampleWasPlaying == snapshot.IsPlaying)
                {
                    var interval = sampleAt - _sampleAt;
                    if (interval >= MinRateInterval)
                    {
                        var candidate = (snapshot.Position - _samplePosition) / interval;
                        if (candidate >= MinRate && candidate <= MaxRate) _derivedRate = candidate;
                    }
                }

                _basePosition = snapshot.Position;
                _baseAt = sampleAt;
                _isPlaying = snapshot.IsPlaying;
                _samplePosition = snapshot.Position;
                _sampleAt = sampleAt;
                _sampleWasPlaying = snapshot.IsPlaying;
                _hasSample = true;

                var drift = snapshot.Position - predictedAtSample;
                if (hadBelief && Math.Abs(drift.Ticks) >= SnapThreshold.Ticks)
                {
                    // Too large to be drift: a seek or a track change. Catch up at
                    // once, and distrust any rate measured across the discontinuity.
                    _derivedRate = null;
                    _rate = ReportedRate(snapshot);
                    _offset = TimeSpan.Zero;
                    _correctionAt = now;
                    return;
                }

                _rate = _derivedRate ?? ReportedRate(snapshot);
            }
            else
            {
                // Same sample, new play state (pause or resume). Re-anchor so the
                // paused wall-clock time is never counted as playback.
                _basePosition = snapshot.Position;
                _baseAt = sampleAt;
                _isPlaying = snapshot.IsPlaying;
            }

            if (hadBelief)
            {
                // Absorb the distance between what we were showing and the newly
                // anchored truth over CorrectionWindow: no jump, just a smooth slide.
                _offset = reference - Extrapolate(now);
            }
            else
            {
                // Nothing was known before this sample, so there is nothing to correct.
                _offset = TimeSpan.Zero;
            }

            _correctionAt = now;
        }
    }

    /// <summary>
    /// The best estimate of the current playback position. Frozen while paused,
    /// clamped to <c>[0, Duration]</c>, and never jumping when a sample lands off
    /// the prediction.
    /// </summary>
    public TimeSpan Position(DateTimeOffset now)
    {
        lock (_gate) return Predict(now);
    }

    /// <summary>
    /// Drop all history. Call on a track change, a seek, a pause/resume, or when the
    /// media session changes: the position before and after those events is not
    /// continuous, so neither the extrapolation nor a measured rate survives them.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _basePosition = TimeSpan.Zero;
            _baseAt = default;
            _rate = 1.0;
            _isPlaying = false;
            _offset = TimeSpan.Zero;
            _correctionAt = default;
            _hasSample = false;
            _samplePosition = TimeSpan.Zero;
            _sampleAt = default;
            _sampleWasPlaying = false;
            _derivedRate = null;
            _duration = TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Extrapolation plus the decaying correction: the value the renderer consumes.
    /// </summary>
    private TimeSpan Predict(DateTimeOffset now)
    {
        var position = Extrapolate(now);

        if (_offset != TimeSpan.Zero)
        {
            var remaining = RemainingCorrection(now);
            if (remaining > 0)
            {
                position += TimeSpan.FromTicks((long)(_offset.Ticks * remaining));
            }
        }

        return Clamp(position);
    }

    /// <summary>Anchor extrapolated to <paramref name="now"/>, ignoring any correction.</summary>
    private TimeSpan Extrapolate(DateTimeOffset now)
    {
        var position = _basePosition;

        if (_isPlaying)
        {
            var elapsed = (now - _baseAt).Ticks;
            if (elapsed > 0)
            {
                if (elapsed > MaxAdvanceTicks) elapsed = MaxAdvanceTicks;
                position += TimeSpan.FromTicks((long)(elapsed * _rate));
            }
        }

        return Clamp(position);
    }

    /// <summary>Fraction of the correction that is still outstanding, 0..1.</summary>
    private double RemainingCorrection(DateTimeOffset now)
    {
        var progressed = (now - _correctionAt).Ticks;
        if (progressed <= 0) return 1.0;
        if (progressed >= CorrectionWindow.Ticks) return 0.0;

        return 1.0 - (double)progressed / CorrectionWindow.Ticks;
    }

    private TimeSpan Clamp(TimeSpan position)
    {
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        if (_duration > TimeSpan.Zero && position > _duration) return _duration;

        return position;
    }

    /// <summary>
    /// The player's own speed, sanity-checked exactly the way
    /// <see cref="PlaybackSnapshot.ExtrapolatedPosition"/> checks it.
    /// </summary>
    private static double ReportedRate(PlaybackSnapshot snapshot)
    {
        var rate = snapshot.PlaybackRate;
        return rate > 0.01 && Math.Abs(rate - 1.0) < 4.0 ? rate : 1.0;
    }
}
