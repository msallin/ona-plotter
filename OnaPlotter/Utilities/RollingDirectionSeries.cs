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

    private readonly TimeWindowBuffer<Sample> _buffer;

    private readonly record struct Sample(DateTime Ts, double AngleRad, double Weight) : ITimedSample;

    public TimeSpan MaxRetention => _buffer.MaxRetention;

    /// <summary>Increments whenever the retained sample set changes
    /// (append, seed, eviction). Lets renderers cache a projection of
    /// the buffer and skip rebuilding it while nothing changed.</summary>
    public long Version => _buffer.Version;

    public RollingDirectionSeries(TimeSpan maxRetention, TimeProvider? time = null)
        => _buffer = new TimeWindowBuffer<Sample>(maxRetention, time);

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
        _buffer.Add(new Sample(_buffer.Now, angleRadians, weight));
    }

    /// <summary>
    /// Pre-load past samples in monotonic-ascending timestamp order
    /// (history seed). All seeded samples carry weight 1.0; the
    /// stationary-COG suppression used by live ingest doesn't apply
    /// to history because the server already aggregated.
    ///
    /// <para>Same merge contract as <see cref="RollingScalarSeries.Seed"/>:
    /// inserts in the gap BEFORE the oldest existing live sample so
    /// the buffer stays monotonic-ascending even when live ingest has
    /// already populated the tail.</para>
    /// </summary>
    public void Seed(IEnumerable<(DateTime Ts, double AngleRad)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        _buffer.Seed(samples
            .Where(s => double.IsFinite(s.AngleRad))
            .Select(s => new Sample(s.Ts, s.AngleRad, 1.0)));
    }

    public int Count => _buffer.Count;

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
        var now = _buffer.Now;
        _buffer.EvictOlderThan(now - MaxRetention);
        int firstIdx = _buffer.FirstIndexSince(now - window);
        if (firstIdx == _buffer.End) return [];
        var result = new List<TimeSeriesSample>(_buffer.End - firstIdx);
        for (int i = firstIdx; i < _buffer.End; i++)
        {
            var s = _buffer[i];
            if (!includeZeroWeight && s.Weight <= 0) continue;
            result.Add(new TimeSeriesSample(s.Ts, s.AngleRad));
        }
        return result;
    }

    /// <summary>
    /// Chart view of the trailing <paramref name="window"/>: at most
    /// about <paramref name="maxVertices"/> stride-sampled vertices plus
    /// min / max / mean over every non-zero-weight in-window sample, read
    /// straight from storage without copying the window.
    /// <paramref name="project"/> converts the stored radians to the
    /// display unit before it is measured; min / max / mean are linear
    /// over the projected values, matching a compass-scale chart axis.
    /// Empty when nothing is in the window or the window exceeds
    /// <see cref="MaxRetention"/>.
    /// </summary>
    public ChartSeries ChartIn(TimeSpan window, int maxVertices, Func<double, double>? project = null)
    {
        if (maxVertices < 2)
            throw new ArgumentOutOfRangeException(nameof(maxVertices), maxVertices, "Need at least two vertices.");
        if (window <= TimeSpan.Zero || window > MaxRetention) return ChartSeries.Empty;
        var now = _buffer.Now;
        _buffer.EvictOlderThan(now - MaxRetention);
        int firstIdx = _buffer.FirstIndexSince(now - window);
        int count = 0;
        for (int i = firstIdx; i < _buffer.End; i++)
            if (_buffer[i].Weight > 0) count++;
        var builder = new ChartSeriesBuilder(count, maxVertices);
        for (int i = firstIdx; i < _buffer.End; i++)
        {
            var s = _buffer[i];
            if (s.Weight <= 0) continue;
            builder.Add(s.Ts, project is null ? s.AngleRad : project(s.AngleRad));
        }
        return builder.Build();
    }

    /// <summary>Circular mean over the trailing <paramref name="window"/>,
    /// in radians (range [-π, +π]). Null on insufficient data /
    /// warmup not satisfied / window past <see cref="MaxRetention"/>.</summary>
    public double? Mean(TimeSpan window, double warmupRatio = RollingScalarSeries.DefaultWarmupRatio)
    {
        if (!_buffer.TryPrepareQuery(window, warmupRatio, out var cutoff)) return null;
        // Walk newest -> oldest with early break; compute weighted
        // sin/cos sums over the in-window samples.
        double sumSin = 0, sumCos = 0, sumW = 0;
        for (int i = _buffer.End - 1; i >= _buffer.Head; i--)
        {
            var s = _buffer[i];
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
        if (!_buffer.TryPrepareQuery(window, warmupRatio, out var cutoff)) return null;
        // First pass (newest -> oldest with break): find the in-window
        // range. Then unwrap forward (oldest -> newest) and fit the
        // regression - two passes total, both bounded by the in-window
        // count, not the full retention.
        int firstIdx = _buffer.FirstIndexSince(cutoff);
        int n = _buffer.End - firstIdx;
        if (n < 5) return null;

        // Unwrap around the first sample so 359 -> 1 reads as +2°,
        // not -358°. Each step is at most ±180° from the previous;
        // anything bigger must have wrapped.
        var times = new double[n];
        var values = new double[n];
        double prev = _buffer[firstIdx].AngleRad * RadToDeg;
        double baseline = prev;
        double running = prev;
        var t0 = _buffer[firstIdx].Ts;
        times[0] = 0;
        values[0] = 0;
        for (int k = 1; k < n; k++)
        {
            int i = firstIdx + k;
            double d = _buffer[i].AngleRad * RadToDeg;
            double delta = d - prev;
            if (delta > 180) delta -= 360;
            else if (delta < -180) delta += 360;
            running += delta;
            times[k] = (_buffer[i].Ts - t0).TotalMinutes;
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
}
