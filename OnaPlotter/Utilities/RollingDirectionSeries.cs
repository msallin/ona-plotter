namespace OnaPlotter.Utilities;

/// <summary>
/// Rolling buffer of (timestamp, angle) samples for a compass
/// direction (COG, TWD, heading - in radians). Mirrors
/// <see cref="RollingScalarSeries"/> but does its math on the unit
/// vector form (sin/cos) so the 350°/10° wrap doesn't poison the
/// mean.
///
/// <para>Storage + query strategy mirrors
/// <see cref="RollingScalarSeries"/>: indexed list with logical
/// head pointer, queries walk newest -&gt; oldest with early break.
/// A 30 s mean on a 180-min retention buffer touches ~30 entries
/// instead of 10,800.</para>
///
/// <para>Two query shapes:
/// <list type="bullet">
///   <item><see cref="Mean"/> - circular mean over a sub-window
///   (radians, range [-π, +π]).</item>
///   <item><see cref="ShiftRateDegPerMin"/> - linear regression
///   slope of the unwrapped angle series. Positive = veering
///   (clockwise); negative = backing. Used by tactical readouts:
///   "TWD veered 5°/5 min -> tack now".</item>
/// </list></para>
///
/// <para>Per-sample weights let callers ignore samples without
/// a clean call shape - e.g. COG should be skipped when SOG is
/// near zero (direction has no meaning when stationary). Pass
/// <c>weight: 0</c> or simply don't call <see cref="Add"/>.</para>
/// </summary>
public sealed class RollingDirectionSeries
{
    private const double RadToDeg = 180.0 / Math.PI;

    private readonly TimeProvider _time;
    private readonly List<Sample> _samples = [];
    /// <summary>Logical start of the live range. Items in
    /// <c>_samples[0.._head)</c> are evicted but not yet shifted out;
    /// <see cref="EvictOlderThan"/> compacts when the dead-prefix
    /// reaches half the list size.</summary>
    private int _head;

    public TimeSpan MaxRetention { get; }

    public RollingDirectionSeries(TimeSpan maxRetention, TimeProvider? time = null)
    {
        if (maxRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxRetention), "must be positive");
        MaxRetention = maxRetention;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Append a sample. Non-finite or negative-weight
    /// samples are silently dropped.</summary>
    /// <param name="angleRadians">Direction in any range (sin/cos
    /// conversion handles wrap).</param>
    /// <param name="weight">Per-sample weight (default 1.0). Set
    /// to 0 to drop a sample inline (still consumes a buffer slot;
    /// prefer omitting the call entirely when possible).</param>
    public void Add(double angleRadians, double weight = 1.0)
    {
        if (!double.IsFinite(angleRadians) || !double.IsFinite(weight) || weight < 0) return;
        var now = _time.GetUtcNow().UtcDateTime;
        _samples.Add(new Sample(now, angleRadians, weight));
        EvictOlderThan(now - MaxRetention);
    }

    /// <summary>
    /// Pre-load past samples in monotonic-ascending timestamp order
    /// (history seed). All seeded samples carry weight 1.0; the
    /// stationary-COG suppression used by live ingest doesn't apply
    /// to history because the server already aggregated.
    ///
    /// <para>Same drop rules as <see cref="RollingScalarSeries.Seed"/>:
    /// non-finite, future, out-of-order, and past-retention samples
    /// are silently skipped. Idempotent under repeat calls.</para>
    /// </summary>
    public void Seed(IEnumerable<(DateTime Ts, double AngleRad)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var now = _time.GetUtcNow().UtcDateTime;
        var cutoff = now - MaxRetention;
        DateTime newest = _samples.Count > 0 ? _samples[^1].Ts : DateTime.MinValue;
        foreach (var (ts, angle) in samples)
        {
            if (!double.IsFinite(angle)) continue;
            if (ts > now) continue;
            if (ts < cutoff) continue;
            if (ts <= newest) continue;
            _samples.Add(new Sample(ts, angle, 1.0));
            newest = ts;
        }
        EvictOlderThan(cutoff);
    }

    public int Count
    {
        get
        {
            var now = _time.GetUtcNow().UtcDateTime;
            EvictOlderThan(now - MaxRetention);
            return _samples.Count - _head;
        }
    }

    /// <summary>
    /// Snapshot of every sample within the trailing
    /// <paramref name="window"/>, oldest to newest, in radians (the
    /// raw stored angle, not the unit-vector form). Zero-weight
    /// samples are skipped by default; pass
    /// <paramref name="includeZeroWeight"/> = true to include them
    /// (chart renderers that want every published reading).
    /// </summary>
    public IReadOnlyList<TimeSeriesSample> SnapshotIn(TimeSpan window, bool includeZeroWeight = false)
    {
        if (window <= TimeSpan.Zero || window > MaxRetention) return [];
        var now = _time.GetUtcNow().UtcDateTime;
        EvictOlderThan(now - MaxRetention);
        if (_samples.Count - _head == 0) return [];
        var cutoff = now - window;
        // Find the lowest in-window index by walking newest -> oldest
        // with early break.
        int firstIdx = _samples.Count;
        for (int i = _samples.Count - 1; i >= _head; i--)
        {
            if (_samples[i].Ts < cutoff) break;
            firstIdx = i;
        }
        if (firstIdx == _samples.Count) return [];
        var result = new List<TimeSeriesSample>(_samples.Count - firstIdx);
        for (int i = firstIdx; i < _samples.Count; i++)
        {
            var s = _samples[i];
            if (!includeZeroWeight && s.Weight <= 0) continue;
            result.Add(new TimeSeriesSample(s.Ts, s.AngleRad));
        }
        return result;
    }

    /// <summary>Circular mean over the trailing <paramref name="window"/>,
    /// in radians (range [-π, +π]). Null on insufficient data /
    /// warmup not satisfied / window past <see cref="MaxRetention"/>.</summary>
    public double? Mean(TimeSpan window, double warmupRatio = RollingScalarSeries.DefaultWarmupRatio)
    {
        if (!TryPrepareQuery(window, warmupRatio, out var cutoff)) return null;
        // Walk newest -> oldest with early break; compute weighted
        // sin/cos sums over the in-window samples.
        double sumSin = 0, sumCos = 0, sumW = 0;
        for (int i = _samples.Count - 1; i >= _head; i--)
        {
            var s = _samples[i];
            if (s.Ts < cutoff) break;
            sumSin += Math.Sin(s.AngleRad) * s.Weight;
            sumCos += Math.Cos(s.AngleRad) * s.Weight;
            sumW += s.Weight;
        }
        if (sumW <= 0) return null;
        return Math.Atan2(sumSin / sumW, sumCos / sumW);
    }

    /// <summary>
    /// Signed shift rate over <paramref name="window"/>, in degrees
    /// per minute. Positive = veering (clockwise on a compass);
    /// negative = backing. Computed by unwrapping the sample series
    /// (so 359° -&gt; 1° reads as +2°, not -358°) and fitting a
    /// linear regression slope. Null on fewer than 5 samples or
    /// warmup not satisfied - a 2-sample slope is too noisy to
    /// publish.
    /// </summary>
    public double? ShiftRateDegPerMin(TimeSpan window, double warmupRatio = RollingScalarSeries.DefaultWarmupRatio)
    {
        if (!TryPrepareQuery(window, warmupRatio, out var cutoff)) return null;
        // First pass (newest -> oldest with break): find the in-window
        // range. Then unwrap forward (oldest -> newest) and fit the
        // regression - two passes total, both bounded by the in-window
        // count, not the full retention.
        int firstIdx = _samples.Count;
        for (int i = _samples.Count - 1; i >= _head; i--)
        {
            if (_samples[i].Ts < cutoff) break;
            firstIdx = i;
        }
        int n = _samples.Count - firstIdx;
        if (n < 5) return null;

        // Unwrap around the first sample so 359 -> 1 reads as +2°,
        // not -358°. Each step is at most ±180° from the previous;
        // anything bigger must have wrapped.
        var times = new double[n];
        var values = new double[n];
        double prev = _samples[firstIdx].AngleRad * RadToDeg;
        double baseline = prev;
        double running = prev;
        var t0 = _samples[firstIdx].Ts;
        times[0] = 0;
        values[0] = 0;
        for (int k = 1; k < n; k++)
        {
            int i = firstIdx + k;
            double d = _samples[i].AngleRad * RadToDeg;
            double delta = d - prev;
            if (delta > 180) delta -= 360;
            else if (delta < -180) delta += 360;
            running += delta;
            times[k] = (_samples[i].Ts - t0).TotalMinutes;
            values[k] = running - baseline;
            prev = d;
        }

        // Linear regression slope: Σ(x-x̄)(y-ȳ) / Σ(x-x̄)²
        double tMean = 0, vMean = 0;
        for (int i = 0; i < n; i++) { tMean += times[i]; vMean += values[i]; }
        tMean /= n;
        vMean /= n;
        double num = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            double tDev = times[i] - tMean;
            num += tDev * (values[i] - vMean);
            den += tDev * tDev;
        }
        if (den <= 0) return null;
        return num / den;   // °/min
    }

    private bool TryPrepareQuery(TimeSpan window, double warmupRatio, out DateTime cutoff)
    {
        cutoff = default;
        if (window <= TimeSpan.Zero || window > MaxRetention) return false;
        if (warmupRatio < 0 || warmupRatio > 1) return false;
        var now = _time.GetUtcNow().UtcDateTime;
        EvictOlderThan(now - MaxRetention);
        if (_samples.Count - _head == 0) return false;
        var oldest = _samples[_head].Ts;
        var coverage = now - oldest;
        if (coverage < window * warmupRatio) return false;
        cutoff = now - window;
        return true;
    }

    private void EvictOlderThan(DateTime cutoff)
    {
        while (_head < _samples.Count && _samples[_head].Ts < cutoff)
        {
            _head++;
        }
        if (_head > 0 && _head >= _samples.Count / 2)
        {
            _samples.RemoveRange(0, _head);
            _head = 0;
        }
    }

    private readonly record struct Sample(DateTime Ts, double AngleRad, double Weight);
}
