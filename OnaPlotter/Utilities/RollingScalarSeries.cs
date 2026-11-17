namespace OnaPlotter.Utilities;

/// <summary>
/// Rolling buffer of (timestamp, scalar) samples retained for up to
/// <see cref="MaxRetention"/>; queries (mean / stats) operate on a
/// caller-chosen sub-window of that retention. One buffer per
/// channel (TWS, AWS, SOG, VMG, depth, ...) is enough to serve every
/// consumer that wants a different window over the same source -
/// the HUD's fixed 30s SOG mean and a future "stats over a helm-
/// picked 1/2/10 min window" page draw from the same data.
///
/// <para>Storage and eviction live in <see cref="TimeWindowBuffer{T}"/>;
/// queries walk newest -&gt; oldest and break as soon as a sample falls
/// outside the requested window.</para>
///
/// <para>For compass angles use <see cref="RollingDirectionSeries"/>;
/// linear averaging here would produce 180° at the 350°/10° wrap.</para>
/// </summary>
public sealed class RollingScalarSeries
{
    /// <summary>Default coverage gate (50%): a query window must be
    /// at least half-spanned by buffered samples before
    /// <see cref="Mean"/> / <see cref="Stats"/> publishes a value.
    /// Stops noisy means from showing on first connect when only
    /// 1-2 samples sit in a 60s window.</summary>
    public const double DefaultWarmupRatio = 0.5;

    private readonly TimeWindowBuffer<Sample> _buffer;

    private readonly record struct Sample(DateTime Ts, double Value) : ITimedSample;

    /// <summary>Longest sub-window any consumer is expected to ask
    /// about. Samples older than this are evicted automatically;
    /// queries with a window past this return null.</summary>
    public TimeSpan MaxRetention => _buffer.MaxRetention;

    /// <summary>Increments whenever the retained sample set changes
    /// (append, seed, eviction). Lets renderers cache a projection of
    /// the buffer and skip rebuilding it while nothing changed.</summary>
    public long Version => _buffer.Version;

    /// <param name="maxRetention">Longest window any consumer
    /// queries - e.g. 60 min for wind (covers WindRose's helm-picked
    /// 60 min chip) or 30 s for SOG (HUD's fixed window).</param>
    /// <param name="time">Time provider; tests inject FakeTimeProvider.</param>
    public RollingScalarSeries(TimeSpan maxRetention, TimeProvider? time = null)
        => _buffer = new TimeWindowBuffer<Sample>(maxRetention, time);

    /// <summary>Append a sample stamped at "now". Non-finite values
    /// (NaN, infinity) are silently dropped - a corrupt sensor read
    /// shouldn't poison the rolling stats.</summary>
    public void Add(double value)
    {
        if (!double.IsFinite(value)) return;
        _buffer.Add(new Sample(_buffer.Now, value));
    }

    /// <summary>
    /// Pre-load past samples in monotonic-ascending timestamp order,
    /// e.g. from a SignalK history fetch. Used to warm the buffer at
    /// page mount so consumers see a populated trend straight away
    /// instead of waiting minutes for live deltas to fill the window.
    /// Non-finite values are dropped (same rule as <see cref="Add"/>);
    /// the merge contract is <see cref="TimeWindowBuffer{T}.Seed"/>'s.
    /// </summary>
    public void Seed(IEnumerable<(DateTime Ts, double Value)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        _buffer.Seed(samples
            .Where(s => double.IsFinite(s.Value))
            .Select(s => new Sample(s.Ts, s.Value)));
    }

    /// <summary>Number of samples retained. Useful for tests and
    /// the warmup-ratio gate at the call site.</summary>
    public int Count => _buffer.Count;

    /// <summary>
    /// Snapshot of every sample within the trailing
    /// <paramref name="window"/>, oldest to newest. Empty when no
    /// samples land in the window or <paramref name="window"/>
    /// exceeds <see cref="MaxRetention"/>. Materialised so callers
    /// (e.g. chart renderers that walk the list multiple times for
    /// y-axis scaling and polyline emission) iterate without
    /// re-evaluating the queue.
    /// </summary>
    public IReadOnlyList<TimeSeriesSample> SnapshotIn(TimeSpan window)
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
            result.Add(new TimeSeriesSample(s.Ts, s.Value));
        }
        return result;
    }

    /// <summary>
    /// Chart view of the trailing <paramref name="window"/>: at most
    /// about <paramref name="maxVertices"/> stride-sampled vertices plus
    /// min / max / mean over every in-window sample, read straight from
    /// storage without copying the window. <paramref name="project"/>
    /// converts each stored value to the display unit (m/s to kn) before
    /// it is measured. Empty when nothing is in the window or the window
    /// exceeds <see cref="MaxRetention"/>.
    /// </summary>
    public ChartSeries ChartIn(TimeSpan window, int maxVertices, Func<double, double>? project = null)
    {
        if (maxVertices < 2)
            throw new ArgumentOutOfRangeException(nameof(maxVertices), maxVertices, "Need at least two vertices.");
        if (window <= TimeSpan.Zero || window > MaxRetention) return ChartSeries.Empty;
        var now = _buffer.Now;
        _buffer.EvictOlderThan(now - MaxRetention);
        int firstIdx = _buffer.FirstIndexSince(now - window);
        var builder = new ChartSeriesBuilder(_buffer.End - firstIdx, maxVertices);
        for (int i = firstIdx; i < _buffer.End; i++)
        {
            var (ts, value) = _buffer[i];
            builder.Add(ts, project is null ? value : project(value));
        }
        return builder.Build();
    }

    /// <summary>
    /// Mean of samples falling inside the trailing <paramref name="window"/>,
    /// or null when:
    /// <list type="bullet">
    ///   <item><paramref name="window"/> exceeds <see cref="MaxRetention"/>
    ///   (we don't have data that far back), or</item>
    ///   <item>no samples land in the window, or</item>
    ///   <item>the buffer has been fed for less than
    ///   <paramref name="warmupRatio"/> × window (warmup gate).</item>
    /// </list></summary>
    public double? Mean(TimeSpan window, double warmupRatio = DefaultWarmupRatio)
    {
        if (!_buffer.TryPrepareQuery(window, warmupRatio, out var cutoff)) return null;
        // Walk newest -> oldest, break on out-of-window. Since ts is
        // monotonic ascending in the list (each Add stamps with the
        // current clock), a single < cutoff hit terminates the loop.
        double sum = 0;
        int n = 0;
        for (int i = _buffer.End - 1; i >= _buffer.Head; i--)
        {
            if (_buffer[i].Ts < cutoff) break;
            sum += _buffer[i].Value;
            n++;
        }
        return n == 0 ? null : sum / n;
    }

    /// <summary>
    /// Full descriptive stats for the trailing <paramref name="window"/>:
    /// mean + min + max + population sigma + gust (max - mean) +
    /// lull (mean - min). Null when warmup not satisfied or fewer
    /// than 2 samples fall in the window (sigma is undefined for n=1).
    /// </summary>
    public WindowStats? Stats(TimeSpan window, double warmupRatio = DefaultWarmupRatio)
    {
        if (!_buffer.TryPrepareQuery(window, warmupRatio, out var cutoff)) return null;
        // First pass (newest -> oldest with break): mean + min + max
        // and the index of the oldest in-window sample.
        double sum = 0, min = double.MaxValue, max = double.MinValue;
        int firstIdx = _buffer.End;
        int n = 0;
        for (int i = _buffer.End - 1; i >= _buffer.Head; i--)
        {
            if (_buffer[i].Ts < cutoff) break;
            firstIdx = i;
            var v = _buffer[i].Value;
            sum += v;
            if (v < min) min = v;
            if (v > max) max = v;
            n++;
        }
        if (n < 2) return null;
        double mean = sum / n;
        // Second pass (oldest -> newest over the in-window range):
        // population variance.
        double sumSq = 0;
        for (int i = firstIdx; i < _buffer.End; i++)
        {
            double d = _buffer[i].Value - mean;
            sumSq += d * d;
        }
        // Population sigma: the helm cares about the spread of THIS
        // window's samples, not an estimate for an unseen larger
        // population (n-1 sample sigma would shift the value with
        // window length more than the underlying variability does).
        double sigma = Math.Sqrt(sumSq / n);
        double gust = Math.Max(0, max - mean);
        double lull = Math.Max(0, mean - min);
        return new WindowStats(mean, min, max, sigma, gust, lull);
    }
}

/// <summary>Stat tuple over a windowed sample set. <see cref="Gust"/>
/// and <see cref="Lull"/> are pre-computed convenience values:
/// gust = Max - Mean, lull = Mean - Min, both clamped at zero.</summary>
public readonly record struct WindowStats(
    double Mean,
    double Min,
    double Max,
    double Sigma,
    double Gust,
    double Lull);

/// <summary>One (timestamp, value) sample in a rolling series.
/// Used by the snapshot APIs on <see cref="RollingScalarSeries"/>
/// and <see cref="RollingDirectionSeries"/> so chart renderers can
/// walk the same data the stats functions average over.</summary>
public readonly record struct TimeSeriesSample(DateTime Time, double Value);
