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
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(2);

    private AisVessel[]? _cachedSnapshot;
    private DateTime _lastPruneTime;
    private int _version;
    private int _snapshotVersion = -1;

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

        _cachedSnapshot = _vessels.Values
            .Where(v => v.Latitude is not null && v.Longitude is not null)
            .ToArray();
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
