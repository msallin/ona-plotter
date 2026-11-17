namespace OnaPlotter.Utilities;

/// <summary>A sample that carries its own timestamp.</summary>
internal interface ITimedSample
{
    DateTime Ts { get; }
}

/// <summary>
/// Time-ordered sample store behind <see cref="RollingScalarSeries"/> and
/// <see cref="RollingDirectionSeries"/>: samples are retained for up to
/// <see cref="MaxRetention"/> and queries walk a caller-chosen sub-window.
///
/// <para>Storage: <see cref="List{T}"/> + a logical <see cref="Head"/>
/// index. Eviction advances the head (O(1) per drop) and physically
/// compacts only when the dead prefix reaches half the list (amortised
/// O(1) per evict). Indexed access lets queries walk newest -&gt; oldest
/// and break as soon as a sample falls outside the window: a 30 s mean on
/// a 180-min retention buffer touches ~30 entries, not 10,800.</para>
///
/// <para>Live indices run <see cref="Head"/> to <see cref="End"/>
/// (exclusive); timestamps are monotonic ascending across them.</para>
/// </summary>
internal sealed class TimeWindowBuffer<T> where T : struct, ITimedSample
{
    private readonly TimeProvider _time;
    private readonly List<T> _samples = [];

    public TimeWindowBuffer(TimeSpan maxRetention, TimeProvider? time)
    {
        if (maxRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxRetention), "must be positive");
        MaxRetention = maxRetention;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Longest sub-window any consumer is expected to ask
    /// about. Older samples are evicted automatically; queries with a
    /// window past this find nothing.</summary>
    public TimeSpan MaxRetention { get; }

    /// <summary>Increments whenever the retained sample set changes
    /// (append, seed, eviction). Lets renderers cache a projection of
    /// the buffer and skip rebuilding it while nothing changed.</summary>
    public long Version { get; private set; }

    public DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>First live index. Items before it are evicted but not yet
    /// shifted out of the list.</summary>
    public int Head { get; private set; }

    /// <summary>One past the last live index.</summary>
    public int End => _samples.Count;

    public T this[int index] => _samples[index];

    /// <summary>Number of live samples, after evicting expired ones.</summary>
    public int Count
    {
        get
        {
            EvictOlderThan(Now - MaxRetention);
            return _samples.Count - Head;
        }
    }

    /// <summary>Append a sample stamped at or after every retained one.</summary>
    public void Add(T sample)
    {
        _samples.Add(sample);
        Version++;
        EvictOlderThan(sample.Ts - MaxRetention);
    }

    /// <summary>
    /// Pre-load past samples in monotonic-ascending timestamp order,
    /// e.g. from a SignalK history fetch, so consumers see a populated
    /// trend straight away instead of waiting minutes for live deltas.
    ///
    /// <para>Seed fills the gap BEFORE the oldest existing sample.
    /// Buffers are singletons that ingest live deltas from app startup,
    /// so by the time a page mounts and seeds there are already live
    /// samples newer than every historical one the server returns.
    /// Inserting at the front preserves the global ascending order the
    /// newest-to-oldest scans depend on.</para>
    ///
    /// <para>Silently dropped: future timestamps, samples at or after the
    /// oldest existing sample (that region is owned by live ingest),
    /// out-of-order or duplicate timestamps within the source, and
    /// samples older than <see cref="MaxRetention"/>. A repeat call sees
    /// the already-seeded data as "existing oldest" and skips it, so
    /// seeding is idempotent and a wider later window extends the buffer
    /// further back.</para>
    /// </summary>
    public void Seed(IEnumerable<T> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var now = Now;
        var cutoff = now - MaxRetention;
        EvictOlderThan(cutoff);

        // Where the live region starts; an empty buffer admits every
        // in-range historical sample.
        DateTime oldestLive = _samples.Count > Head ? _samples[Head].Ts : DateTime.MaxValue;

        var fresh = new List<T>();
        DateTime prev = DateTime.MinValue;
        foreach (var sample in samples)
        {
            var ts = sample.Ts;
            if (ts > now) continue;
            if (ts < cutoff) continue;
            if (ts >= oldestLive) continue;
            if (ts <= prev) continue;
            fresh.Add(sample);
            prev = ts;
        }

        if (fresh.Count == 0) return;

        // Compact the dead prefix so InsertRange's index 0 lands at the
        // actual oldest live sample, not at a stale evicted slot.
        Compact();
        _samples.InsertRange(0, fresh);
        Version++;
    }

    /// <summary>Lowest live index at or after <paramref name="cutoff"/>,
    /// or <see cref="End"/> when none is. Walks newest -&gt; oldest and
    /// stops at the first sample outside the window.</summary>
    public int FirstIndexSince(DateTime cutoff)
    {
        int firstIdx = _samples.Count;
        for (int i = _samples.Count - 1; i >= Head; i--)
        {
            if (_samples[i].Ts < cutoff) break;
            firstIdx = i;
        }
        return firstIdx;
    }

    /// <summary>Validates the query, evicts, and gates on warmup: the
    /// buffer must span at least <paramref name="warmupRatio"/> of the
    /// window, so a noisy value does not publish on first connect off one
    /// or two samples. False means the caller reports "no value";
    /// otherwise <paramref name="cutoff"/> is the start of the window.</summary>
    public bool TryPrepareQuery(TimeSpan window, double warmupRatio, out DateTime cutoff)
    {
        cutoff = default;
        if (window <= TimeSpan.Zero || window > MaxRetention) return false;
        if (warmupRatio < 0 || warmupRatio > 1) return false;
        var now = Now;
        EvictOlderThan(now - MaxRetention);
        if (_samples.Count - Head == 0) return false;
        var coverage = now - _samples[Head].Ts;
        if (coverage < window * warmupRatio) return false;
        cutoff = now - window;
        return true;
    }

    /// <summary>Drops samples older than <paramref name="cutoff"/> (and
    /// publishes a new <see cref="Version"/>); queries call it with
    /// <c>now - MaxRetention</c> before reading.</summary>
    public void EvictOlderThan(DateTime cutoff)
    {
        // Advance head past dead samples - O(k) per call where k is the
        // number falling off the front (usually 0 or 1).
        int before = Head;
        while (Head < _samples.Count && _samples[Head].Ts < cutoff)
            Head++;
        if (Head != before) Version++;
        // Compact when the dead prefix reaches half the list size,
        // amortising the O(N) shift to O(1) per eviction.
        if (Head > 0 && Head >= _samples.Count / 2)
            Compact();
    }

    private void Compact()
    {
        if (Head == 0) return;
        _samples.RemoveRange(0, Head);
        Head = 0;
    }
}
