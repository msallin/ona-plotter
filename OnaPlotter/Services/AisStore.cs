// Thread-safe store for AIS vessel targets. Prunes stale entries on a throttled schedule.

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
    public void Apply(string context, string path, object? value)
    {
        var vessel = _vessels.GetOrAdd(context, ctx =>
        {
            var v = new AisVessel(ctx);
            v.Mmsi = AisVessel.ExtractMmsi(ctx);
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
