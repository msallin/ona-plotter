using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// In-memory chunk cache for the SignalK History API. Sits between
/// <see cref="TrackApi.GetServerTrackPointsAsync"/> and
/// <c>/signalk/v2/api/history/values</c>: each paginated sub-request
/// is keyed by its absolute (paths, resolution, from, to[, bbox])
/// tuple, and a hit short-circuits both the round-trip and the JSON
/// parse. Lives for the SPA session (DI singleton in WASM); not
/// persisted -- a tab reload starts cold.
///
/// <para>Why per-chunk and not per-query: the History page emits
/// sliding-window queries ("last 7 days") whose absolute bounds shift
/// every tick, so a query-level cache key would always miss. The
/// pagination loop already breaks long windows into stable
/// ~4-hour chunks; those bounds DON'T move once a chunk's <c>to</c>
/// has slipped into the past, so chunk-level caching reuses ~95% of
/// data on re-loads of the same dropdown selection.</para>
///
/// <para>Live-tail rule: any chunk whose upper bound is within
/// <see cref="HeadFreshness"/> of "now" is treated as still being
/// filled by the boat -- TryGet returns null AND Set is a no-op.
/// Skips both directions because (a) reading stale "you haven't
/// moved" data while the boat is underway is a confusing failure
/// mode, and (b) writing the still-incomplete head chunk would
/// poison the cache for tomorrow's reload of the same window.</para>
///
/// <para>Eviction: bounded LRU with <see cref="MaxEntries"/> caps so
/// a long session doesn't leak. Per-chunk size is small (~480 rows ×
/// the TrackPoint shape = a few hundred KB at most) and 200 entries
/// = ~10 MB worst case; well inside any realistic browser budget.</para>
///
/// <para>Threading: Blazor WASM is single-threaded; no locks needed.
/// If the runtime ever moves to multi-threaded WASM, the LinkedList
/// + Dictionary mutations would need a SemaphoreSlim or a
/// ConcurrentDictionary backing.</para>
/// </summary>
public sealed class HistoryCache
{
    /// <summary>Treat the most recent <see cref="HeadFreshness"/>
    /// of wall-clock as "live" -- a chunk that overlaps the last
    /// minute is refetched, not served from cache. 1 min lets a
    /// rapid History toggle avoid the round-trip but doesn't hold
    /// stale "boat hasn't moved" data through an underway leg.</summary>
    public static readonly TimeSpan HeadFreshness = TimeSpan.FromMinutes(1);

    /// <summary>LRU eviction cap. 200 chunks × typical ~480 rows ≈
    /// 96k samples -- a comfortable ceiling that catches reasonable
    /// re-load patterns (multiple windows, repeated views) without
    /// letting a months-long browser session balloon.</summary>
    public const int MaxEntries = 200;

    /// <summary>Cache key. Records all the parameters that affect
    /// the API response shape: a query that differs in any field
    /// must miss. Bbox is keyed by its serialised invariant-culture
    /// form (or empty for "no bbox") so the bbox match is exact and
    /// safe across helm locales.</summary>
    public sealed record Key(
        string Paths,
        string Resolution,
        DateTime FromUtc,
        DateTime ToUtc,
        string BboxKey)
    {
        /// <summary>Build a key from the chunked-fetch parameters.
        /// Centralised so the cache user (TrackApi) and the tests
        /// can't drift on the bbox-serialisation rule.</summary>
        public static Key From(
            string paths, string resolution,
            DateTime fromUtc, DateTime toUtc,
            TrackBbox? bbox)
        {
            string bboxKey = bbox is TrackBbox b
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3}", b.South, b.West, b.North, b.East)
                : string.Empty;
            return new Key(
                paths,
                resolution,
                DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc),
                DateTime.SpecifyKind(toUtc, DateTimeKind.Utc),
                bboxKey);
        }
    }

    private readonly TimeProvider _time;
    private readonly LinkedList<KeyValuePair<Key, TrackPoint[]>> _lru = new();
    private readonly Dictionary<Key, LinkedListNode<KeyValuePair<Key, TrackPoint[]>>> _index = new();

    /// <summary>Cumulative cache hit count. Public for diagnostic
    /// surfaces (a future debug overlay) and for the tests that pin
    /// "second load reuses chunks". Not reset on eviction so the
    /// counters reflect session-long behaviour.</summary>
    public int Hits { get; private set; }

    /// <summary>Cumulative cache miss count. Counterpart to
    /// <see cref="Hits"/>; ratio is the easy diagnostic metric.</summary>
    public int Misses { get; private set; }

    /// <summary>Live entry count (after evictions). Bounded by
    /// <see cref="MaxEntries"/>. Useful both as a sanity check in
    /// tests and as a future "show cache fill" UI.</summary>
    public int Count => _index.Count;

    public HistoryCache(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Lookup. Returns null on miss, on a stale (live-tail)
    /// chunk, or when the key isn't present. Hits are counted only
    /// when the value is actually returned, not when the entry was
    /// found-but-rejected by the freshness rule.</summary>
    public TrackPoint[]? TryGet(Key key)
    {
        if (IsLiveTail(key))
        {
            Misses++;
            return null;
        }
        if (_index.TryGetValue(key, out var node))
        {
            // Promote to MRU on access so LRU eviction protects
            // recently-used entries during a long session.
            _lru.Remove(node);
            _lru.AddFirst(node);
            Hits++;
            return node.Value.Value;
        }
        Misses++;
        return null;
    }

    /// <summary>Store. No-op for live-tail chunks (they'd be wrong
    /// tomorrow). Existing entries for the same key are replaced
    /// in place; LRU position is reset to MRU. Eviction trims to
    /// <see cref="MaxEntries"/> from the LRU tail after insert.</summary>
    public void Set(Key key, TrackPoint[] value)
    {
        if (IsLiveTail(key)) return;

        if (_index.TryGetValue(key, out var existing))
        {
            _lru.Remove(existing);
        }
        var node = new LinkedListNode<KeyValuePair<Key, TrackPoint[]>>(
            new KeyValuePair<Key, TrackPoint[]>(key, value));
        _lru.AddFirst(node);
        _index[key] = node;

        while (_lru.Count > MaxEntries)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _index.Remove(last.Value.Key);
        }
    }

    /// <summary>Drop everything. Exposed for the (future) "force
    /// refresh" button and for test isolation -- a cache instance
    /// that survives between tests would otherwise smuggle state.</summary>
    public void Clear()
    {
        _lru.Clear();
        _index.Clear();
        Hits = 0;
        Misses = 0;
    }

    private bool IsLiveTail(Key key)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return key.ToUtc > now - HeadFreshness;
    }
}
