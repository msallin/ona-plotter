using System.Collections.Concurrent;
using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// Singleton store for AIS Aids to Navigation. Mirrors
/// <see cref="AisStore"/>'s shape (concurrent dictionary keyed by full
/// SignalK context, snapshot cache rebuilt on version bump) so the
/// rendering and alarm pipelines can treat AtoNs uniformly with
/// vessels.
/// <para>
/// AtoNs don't move (mostly) and don't generate per-second deltas, so
/// the staleness sweep is gentler: 24 h before pruning. A real-world
/// AIS Type 21 broadcast cadence is 3 min; missing 24 h of broadcasts
/// almost certainly means the boat moved out of receiver range.
/// </para>
/// </summary>
public sealed class AtonStore
{
    private readonly ConcurrentDictionary<string, Aton> _atons = new();
    // AtoN broadcasts are roughly every 3 min per AIS standard; 24 h is
    // ~480 missed transmissions, well past any normal gap. Keeps the
    // store clean across multi-day passages where a buoy was visible
    // at the start but is now well astern.
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(24);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(15);

    private Aton[]? _cachedSnapshot;
    private DateTime _lastPruneTime;
    private int _version;
    private int _snapshotVersion = -1;

    /// <summary>Fired when the store's contents change (apply to existing
    /// or new AtoN, prune sweep). UI subscribers re-render the marker
    /// layer; alarm subscribers re-evaluate proximity.</summary>
    public event Action? OnAtonsUpdated;

    /// <summary>Apply a delta. Creates the AtoN entry on first sight,
    /// then forwards to <see cref="Aton.Apply"/>. Bumps the version on
    /// changes so <see cref="GetAtons"/> rebuilds its snapshot lazily.</summary>
    public void Apply(string context, string path, object? value)
    {
        var aton = _atons.GetOrAdd(context, ctx => new Aton(ctx));
        if (aton.Apply(path, value))
        {
            Interlocked.Increment(ref _version);
            OnAtonsUpdated?.Invoke();
        }
    }

    /// <summary>Returns a cached snapshot of AtoNs that have a position.
    /// Snapshot is rebuilt only when data has changed (version-bump
    /// invalidates the cache).</summary>
    public Aton[] GetAtons()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPruneTime > PruneInterval)
        {
            _lastPruneTime = now;
            PruneStale(now);
        }

        int currentVersion = Volatile.Read(ref _version);
        if (_cachedSnapshot is not null && _snapshotVersion == currentVersion)
            return _cachedSnapshot;

        _cachedSnapshot = _atons.Values
            .Where(a => a.Latitude is not null && a.Longitude is not null)
            .ToArray();
        _snapshotVersion = currentVersion;
        return _cachedSnapshot;
    }

    public int Count => _atons.Count;

    /// <summary>Drop everything. Called from SignalkClient on a websocket
    /// drop so a stale AtoN from before the disconnect doesn't haunt
    /// the map while we're offline - the server re-publishes the
    /// active set on reconnect anyway. Resets the prune-timer too so
    /// the post-Reset state is symmetric with a freshly-constructed
    /// store.</summary>
    public void Reset()
    {
        if (_atons.IsEmpty) return;
        _atons.Clear();
        _lastPruneTime = default;
        Interlocked.Increment(ref _version);
        OnAtonsUpdated?.Invoke();
    }

    private void PruneStale(DateTime now)
    {
        var cutoff = now - StaleThreshold;
        bool changed = false;
        foreach (var kvp in _atons)
        {
            if (kvp.Value.LastSeen < cutoff)
            {
                _atons.TryRemove(kvp.Key, out _);
                changed = true;
            }
        }
        if (changed)
        {
            Interlocked.Increment(ref _version);
            OnAtonsUpdated?.Invoke();
        }
    }
}
