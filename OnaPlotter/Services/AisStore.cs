using System.Collections.Concurrent;
using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// Thread-safe store for AIS vessel targets received from SignalK.
/// Maintains a snapshot cache that is rebuilt only when data changes,
/// and prunes stale entries that haven't been seen for 10 minutes.
/// </summary>
public sealed class AisStore
{
    private readonly ConcurrentDictionary<string, AisVessel> _vessels = new();
    private readonly HashSet<string> _buddyContexts = new(StringComparer.Ordinal);
    private readonly Lock _buddyLock = new();
    // Block-listed contexts (own-boat, identified after the fact). Uses
    // ConcurrentDictionary so reads in Apply() on the WebSocket thread
    // don't race a write from Evict() or a future UI-thread caller.
    private readonly ConcurrentDictionary<string, byte> _blocklist = new(StringComparer.Ordinal);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(2);

    private AisVessel[]? _cachedSnapshot;
    private DateTime _lastPruneTime;
    private int _version;
    private int _snapshotVersion = -1;

    /// <summary>Monotonic per-Apply / per-mutation counter. AisPushService
    /// reads this at the start of each tick and skips the JS interop +
    /// snapshot rebuild when the value hasn't advanced since the previous
    /// push AND the own-vessel geometry fed into CPA / COLREGS hasn't
    /// changed either. 200-vessel harbour at 3 s push cadence dropped
    /// ~14 short-lived allocs per vessel per tick on idle ticks.</summary>
    public int Version => Volatile.Read(ref _version);

    public event Action? OnAisUpdated;

    /// <summary>
    /// Gets or creates a vessel entry for the given SignalK context, then applies the path/value.
    /// </summary>
    /// <summary>Prefix used for radar-target contexts: <c>radar.&lt;radarId&gt;.&lt;targetId&gt;</c>.
    /// Radar contexts are synthesised by <see cref="SignalkClient"/>; they
    /// live in the same store as AIS vessels so the alarm/CPA/COLREGS
    /// pipeline treats both uniformly.</summary>
    public const string RadarContextPrefix = "radar.";

    public void Apply(string context, string path, object? value)
    {
        // Block-list guard: once SignalkClient identifies the self-URN,
        // any further deltas on that context are dropped so own-boat
        // can't re-emerge in the AIS list after a manual or late evict.
        if (_blocklist.ContainsKey(context)) return;
        var vessel = _vessels.GetOrAdd(context, ctx =>
        {
            var v = new AisVessel(ctx);
            if (ctx.StartsWith(RadarContextPrefix, StringComparison.Ordinal))
            {
                v.Source = TargetSource.Radar;
                // Name the radar target using its target-id portion so the
                // map label shows something like "RDR-T123" instead of a
                // raw context string.
                v.Name = "RDR-" + ctx[(ctx.LastIndexOf('.') + 1)..];
            }
            else
            {
                v.Mmsi = AisVessel.ExtractMmsi(ctx);
            }
            lock (_buddyLock)
            {
                v.IsBuddy = _buddyContexts.Contains(ctx);
            }
            return v;
        });

        if (vessel.Apply(path, value))
        {
            Interlocked.Increment(ref _version);
            OnAisUpdated?.Invoke();
        }
    }

    /// <summary>
    /// Returns a cached snapshot of all vessels that have a position.
    /// Snapshot is rebuilt only when data has changed.
    /// </summary>
    public AisVessel[] GetVessels()
    {
        // Throttled pruning: at most once per PruneInterval.
        var now = DateTime.UtcNow;
        if (now - _lastPruneTime > PruneInterval)
        {
            _lastPruneTime = now;
            PruneStale(now);
        }

        // Return cached snapshot if version hasn't changed.
        int currentVersion = Volatile.Read(ref _version);
        if (_cachedSnapshot is not null && _snapshotVersion == currentVersion)
            return _cachedSnapshot;

        // Pre-sized array + manual foreach so the snapshot rebuild
        // (called per-AIS-push tick from the map page) doesn't allocate
        // an enumerator + iterator pair on every invocation.
        //
        // Concurrency: production runs single-threaded WASM, but the
        // test fixture (`GetVessels_ConcurrentApplyAndRead_*`) drives
        // Apply() on parallel threads to assert the snapshot doesn't
        // tear. ConcurrentDictionary.Values enumerates the LIVE state,
        // so a concurrent Apply() may grow the dictionary mid-foreach
        // and our pre-sized buf can fall short. The `n >= buf.Length`
        // guard backstops the count snapshot; whatever doesn't fit
        // this tick lands on the next one (the version counter then
        // forces a fresh rebuild). The earlier "single-thread WASM"
        // comment was wrong about test-time invariants.
        int max = _vessels.Count;
        var buf = new AisVessel[max];
        int n = 0;
        foreach (var v in _vessels.Values)
        {
            if (n >= buf.Length) break;
            if (v.Latitude is not null && v.Longitude is not null)
                buf[n++] = v;
        }
        _cachedSnapshot = (n == max) ? buf : buf.AsSpan(0, n).ToArray();
        _snapshotVersion = currentVersion;
        return _cachedSnapshot;
    }

    public int Count => _vessels.Count;

    /// <summary>
    /// Replaces the buddy-context set and retags every tracked vessel. Fed
    /// from the REST seed of sbender9/signalk-buddylist-plugin at startup.
    /// Vessels that join later are tagged on creation in <see cref="Apply"/>.
    /// <para>
    /// Concurrency note: this method holds <c>_buddyLock</c> while it
    /// iterates the vessel dictionary, which blocks any concurrent
    /// UpdateBuddies call. Apply() reads the buddy set only while creating
    /// a new vessel (also under the lock), so a vessel that arrives during
    /// an UpdateBuddies call is guaranteed to see either the old or the
    /// new buddy set consistently - never a half-applied one.
    /// </para>
    /// </summary>
    public void UpdateBuddies(IEnumerable<string> buddyContexts)
    {
        bool changed = false;
        lock (_buddyLock)
        {
            _buddyContexts.Clear();
            foreach (var ctx in buddyContexts) _buddyContexts.Add(ctx);

            foreach (var v in _vessels.Values)
            {
                bool shouldBe = _buddyContexts.Contains(v.Context);
                if (v.IsBuddy != shouldBe)
                {
                    v.IsBuddy = shouldBe;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Interlocked.Increment(ref _version);
            OnAisUpdated?.Invoke();
        }
    }

    /// <summary>
    /// Removes a context from the store - used once SignalkClient learns
    /// the self-URN AFTER own-boat deltas were already routed to AIS
    /// (hello message arrives late, or server doesn't send "vessels.self"
    /// at all and only uses the URN form). Also registers the context in
    /// a blocklist so later deltas on the same context are ignored.
    /// </summary>
    public void Evict(string context)
    {
        if (string.IsNullOrEmpty(context)) return;
        _blocklist.TryAdd(context, 0);
        if (_vessels.TryRemove(context, out _))
            Interlocked.Increment(ref _version);
        OnAisUpdated?.Invoke();
    }

    /// <summary>
    /// Non-blocklisting remove. Used for radar target deletions
    /// (Signal K Radar API emits <c>value: null</c> on a target
    /// path once tracking is cancelled) where the same id may come
    /// back later and we MUST re-accept the next delta. Evict's
    /// blocklist would latch the target out permanently.
    /// </summary>
    public void RemoveContext(string context)
    {
        if (string.IsNullOrEmpty(context)) return;
        if (_vessels.TryRemove(context, out _))
        {
            Interlocked.Increment(ref _version);
            OnAisUpdated?.Invoke();
        }
    }

    /// <summary>
    /// Overwrites the vessel name, typically from an external enrichment source
    /// (MarineTraffic / VesselFinder lookup) when SignalK hasn't yet delivered
    /// an AIS static-data message. No-op if the name is unchanged or the vessel
    /// already has a name from SignalK.
    /// </summary>
    public void SetName(string context, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!_vessels.TryGetValue(context, out var vessel)) return;
        if (!string.IsNullOrEmpty(vessel.Name)) return;

        vessel.Name = name;
        Interlocked.Increment(ref _version);
        OnAisUpdated?.Invoke();
    }

    private void PruneStale(DateTime now)
    {
        var cutoff = now - StaleThreshold;
        foreach (var kvp in _vessels)
        {
            if (kvp.Value.LastSeen < cutoff)
            {
                _vessels.TryRemove(kvp.Key, out _);
                Interlocked.Increment(ref _version);
            }
        }
    }
}
