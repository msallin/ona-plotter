namespace OnaPlotter.Utilities;

/// <summary>
/// Rolling buffer of (timestamp, scalar) samples retained for up to
/// <see cref="MaxRetention"/>; queries (mean / stats) operate on a
/// caller-chosen sub-window of that retention. One buffer per
/// channel (TWS, AWS, SOG, VMG, depth, ...) is enough to serve every
/// consumer that wants a different window over the same source --
/// the HUD's fixed 30s SOG mean and a future "stats over a helm-
/// picked 1/2/10 min window" page draw from the same data.
///
/// <para>Storage: <see cref="List{T}"/> + a logical <c>_head</c>
/// index. Eviction advances the head pointer (O(1) per drop) and
/// physically compacts only when more than half the list is dead
/// (amortised O(1) per evict). Indexed access lets queries walk
/// newest -&gt; oldest and break as soon as a sample falls outside
/// the requested window: a 30s mean on a 180-min retention buffer
/// touches ~30 entries, not 10,800.</para>
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

    private readonly TimeProvider _time;
    private readonly List<(DateTime ts, double value)> _samples = [];
    /// <summary>Logical start of the live range. Items in
    /// <c>_samples[0.._head)</c> are evicted but not yet shifted out;
    /// <see cref="EvictOlderThan"/> compacts when the dead-prefix
    /// reaches half the list size, amortising the shift cost.</summary>
    private int _head;

    /// <summary>Longest sub-window any consumer is expected to ask
    /// about. Samples older than this are evicted automatically;
    /// queries with a window past this return null.</summary>
    public TimeSpan MaxRetention { get; }

    /// <param name="maxRetention">Longest window any consumer
    /// queries -- e.g. 60 min for wind (covers WindRose's helm-picked
    /// 60 min chip) or 30 s for SOG (HUD's fixed window).</param>
    /// <param name="time">Time provider; tests inject FakeTimeProvider.</param>
    public RollingScalarSeries(TimeSpan maxRetention, TimeProvider? time = null)
    {
        if (maxRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxRetention), "must be positive");
        MaxRetention = maxRetention;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Append a sample stamped at "now". Non-finite values
    /// (NaN, infinity) are silently dropped -- a corrupt sensor read
    /// shouldn't poison the rolling stats.</summary>
    public void Add(double value)
    {
        if (!double.IsFinite(value)) return;
        var now = _time.GetUtcNow().UtcDateTime;
        _samples.Add((now, value));
        EvictOlderThan(now - MaxRetention);
    }

    /// <summary>Number of samples retained. Useful for tests and
    /// the warmup-ratio gate at the call site.</summary>
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
        var now = _time.GetUtcNow().UtcDateTime;
        EvictOlderThan(now - MaxRetention);
        if (_samples.Count - _head == 0) return [];
        var cutoff = now - window;
        // Walk newest -> oldest to find the lowest in-window index
        // (break early); then materialise oldest -> newest for the
        // chart polyline.
        int firstIdx = _samples.Count;
        for (int i = _samples.Count - 1; i >= _head; i--)
        {
            if (_samples[i].ts < cutoff) break;
            firstIdx = i;
        }
        if (firstIdx == _samples.Count) return [];
        var result = new List<TimeSeriesSample>(_samples.Count - firstIdx);
        for (int i = firstIdx; i < _samples.Count; i++)
        {
            var s = _samples[i];
            result.Add(new TimeSeriesSample(s.ts, s.value));
        }
        return result;
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
        if (!TryPrepareQuery(window, warmupRatio, out var cutoff)) return null;
        // Walk newest -> oldest, break on out-of-window. Since ts is
        // monotonic ascending in the list (each Add stamps with the
        // current clock), a single < cutoff hit terminates the loop.
        double sum = 0;
        int n = 0;
        for (int i = _samples.Count - 1; i >= _head; i--)
        {
            if (_samples[i].ts < cutoff) break;
            sum += _samples[i].value;
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
        if (!TryPrepareQuery(window, warmupRatio, out var cutoff)) return null;
        // First pass (newest -> oldest with break): mean + min + max
        // and the index of the oldest in-window sample.
        double sum = 0, min = double.MaxValue, max = double.MinValue;
        int firstIdx = _samples.Count;
        int n = 0;
        for (int i = _samples.Count - 1; i >= _head; i--)
        {
            if (_samples[i].ts < cutoff) break;
            firstIdx = i;
            var v = _samples[i].value;
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
        for (int i = firstIdx; i < _samples.Count; i++)
        {
            double d = _samples[i].value - mean;
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

    /// <summary>Validates the query, evicts, gates on warmup, and
    /// returns the cutoff timestamp for the in-window scan. False
    /// -> the caller returns null.</summary>
    private bool TryPrepareQuery(TimeSpan window, double warmupRatio, out DateTime cutoff)
    {
        cutoff = default;
        if (window <= TimeSpan.Zero || window > MaxRetention) return false;
        if (warmupRatio < 0 || warmupRatio > 1) return false;
        var now = _time.GetUtcNow().UtcDateTime;
        EvictOlderThan(now - MaxRetention);
        if (_samples.Count - _head == 0) return false;
        var oldest = _samples[_head].ts;
        var coverage = now - oldest;
        if (coverage < window * warmupRatio) return false;
        cutoff = now - window;
        return true;
    }

    private void EvictOlderThan(DateTime cutoff)
    {
        // Advance head past dead samples -- O(k) per call where k is
        // the number falling off the front (usually 0 or 1).
        while (_head < _samples.Count && _samples[_head].ts < cutoff)
        {
            _head++;
        }
        // Compact when the dead prefix reaches half the list size --
        // amortises the O(N) shift to O(1) per eviction.
        if (_head > 0 && _head >= _samples.Count / 2)
        {
            _samples.RemoveRange(0, _head);
            _head = 0;
        }
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
