namespace OnaPlotter.Utilities;

/// <summary>
/// Rolling buffer of (timestamp, angle) samples for a compass
/// direction (COG, TWD, heading -- in radians). Mirrors
/// <see cref="RollingScalarSeries"/> but does its math on the unit
/// vector form (sin/cos) so the 350°/10° wrap doesn't poison the
/// mean.
///
/// <para>Two query shapes:
/// <list type="bullet">
///   <item><see cref="Mean"/> -- circular mean over a sub-window
///   (radians, range [-π, +π]).</item>
///   <item><see cref="ShiftRateDegPerMin"/> -- linear regression
///   slope of the unwrapped angle series. Positive = veering
///   (clockwise); negative = backing. Used by tactical readouts:
///   "TWD veered 5°/5 min -> tack now".</item>
/// </list></para>
///
/// <para>Per-sample weights let callers ignore samples without
/// a clean call shape -- e.g. COG should be skipped when SOG is
/// near zero (direction has no meaning when stationary). Pass
/// <c>weight: 0</c> or simply don't call <see cref="Add"/>.</para>
/// </summary>
public sealed class RollingDirectionSeries
{
    private const double TwoPi = Math.PI * 2;
    private const double Pi = Math.PI;
    private const double RadToDeg = 180.0 / Math.PI;

    private readonly TimeProvider _time;
    private readonly Queue<Sample> _samples = new();

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
        _samples.Enqueue(new Sample(now, angleRadians, weight));
        EvictOlderThan(now - MaxRetention);
    }

    public int Count
    {
        get
        {
            var now = _time.GetUtcNow().UtcDateTime;
            EvictOlderThan(now - MaxRetention);
            return _samples.Count;
        }
    }

    /// <summary>
    /// Snapshot of every sample within the trailing
    /// <paramref name="window"/>, oldest to newest, in radians (the
    /// raw stored angle, not the unit-vector form). Zero-weight
    /// samples are included so callers that want every published
    /// reading (chart renderers) see them; callers that want only
    /// "real" directions can filter by themselves -- but typically
    /// a zero-weight sample is just a stationary frame whose angle
    /// is GPS-noise garbage, so most chart consumers will want to
    /// skip those. Use the <paramref name="includeZeroWeight"/>
    /// flag to opt in.
    /// </summary>
    public IReadOnlyList<TimeSeriesSample> SnapshotIn(TimeSpan window, bool includeZeroWeight = false)
    {
        if (window <= TimeSpan.Zero || window > MaxRetention) return [];
        var now = _time.GetUtcNow().UtcDateTime;
        EvictOlderThan(now - MaxRetention);
        if (_samples.Count == 0) return [];
        var cutoff = now - window;
        var result = new List<TimeSeriesSample>(_samples.Count);
        foreach (var s in _samples)
        {
            if (s.Ts < cutoff) continue;
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
        if (!TryWindowSnapshot(window, warmupRatio, out var snap)) return null;
        double sumSin = 0, sumCos = 0, sumW = 0;
        foreach (var s in snap)
        {
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
    /// warmup not satisfied -- a 2-sample slope is too noisy to
    /// publish.
    /// </summary>
    public double? ShiftRateDegPerMin(TimeSpan window, double warmupRatio = RollingScalarSeries.DefaultWarmupRatio)
    {
        if (!TryWindowSnapshot(window, warmupRatio, out var snap)) return null;
        var arr = snap.ToArray();
        if (arr.Length < 5) return null;

        // Unwrap around the first sample so 359 -> 1 reads as +2°,
        // not -358°. Each step is at most ±180° from the previous;
        // anything bigger must have wrapped.
        double prev = arr[0].AngleRad * RadToDeg;
        double baseline = prev;
        var times = new double[arr.Length];
        var values = new double[arr.Length];
        double running = prev;
        var t0 = arr[0].Ts;
        times[0] = 0;
        values[0] = 0;
        for (int i = 1; i < arr.Length; i++)
        {
            double d = arr[i].AngleRad * RadToDeg;
            double delta = d - prev;
            if (delta > 180) delta -= 360;
            else if (delta < -180) delta += 360;
            running += delta;
            times[i] = (arr[i].Ts - t0).TotalMinutes;
            values[i] = running - baseline;
            prev = d;
        }

        // Linear regression slope: Σ(x-x̄)(y-ȳ) / Σ(x-x̄)²
        double tMean = 0, vMean = 0;
        for (int i = 0; i < arr.Length; i++) { tMean += times[i]; vMean += values[i]; }
        tMean /= arr.Length;
        vMean /= arr.Length;
        double num = 0, den = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            double tDev = times[i] - tMean;
            num += tDev * (values[i] - vMean);
            den += tDev * tDev;
        }
        if (den <= 0) return null;
        return num / den;   // °/min
    }

    private bool TryWindowSnapshot(TimeSpan window, double warmupRatio, out IEnumerable<Sample> snap)
    {
        snap = [];
        if (window <= TimeSpan.Zero || window > MaxRetention) return false;
        if (warmupRatio < 0 || warmupRatio > 1) return false;
        var now = _time.GetUtcNow().UtcDateTime;
        EvictOlderThan(now - MaxRetention);
        if (_samples.Count == 0) return false;
        var oldest = _samples.Peek().Ts;
        var coverage = now - oldest;
        if (coverage < window * warmupRatio) return false;
        var cutoff = now - window;
        snap = _samples.Where(s => s.Ts >= cutoff && s.Weight > 0);
        return true;
    }

    private void EvictOlderThan(DateTime cutoff)
    {
        while (_samples.Count > 0 && _samples.Peek().Ts < cutoff)
        {
            _samples.Dequeue();
        }
    }

    private readonly record struct Sample(DateTime Ts, double AngleRad, double Weight);
}
