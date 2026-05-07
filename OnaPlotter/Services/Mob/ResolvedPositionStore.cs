using System.Text.Json;

namespace OnaPlotter.Services.Mob;

/// <summary>
/// Persistable cache of resolved MOB positions keyed by serverId.
///
/// <para>signalk-server's POST /signalk/v2/api/notifications/mob
/// endpoint discards the request body's <c>position</c> field. The
/// WS echo (and the GET /notifications recovery) therefore arrive
/// without coords, so the chart marker would silently disappear on
/// any session boundary that loses the local synthetic. This store
/// is the recovery path: <see cref="MobService"/> writes here when
/// reconcile copies the helm-recorded position onto the server twin,
/// and reads here on init when the server's list returns a position-
/// less server twin.</para>
///
/// <para>localStorage-backed via <see cref="IKeyValueStore"/>. The
/// in-memory dictionary is the source of truth between persists;
/// each mutation fires a fire-and-forget write so the next session
/// (or tab, on shared localStorage) can recover. KV failure is
/// tolerated: the cache degrades to "session-scoped only".</para>
/// </summary>
public sealed class ResolvedPositionStore
{
    internal const string StorageKey = "mob.resolvedPositions.v1";

    private readonly IKeyValueStore _kv;
    private readonly Dictionary<string, (double Lat, double Lon)> _cache =
        new(StringComparer.Ordinal);

    public ResolvedPositionStore(IKeyValueStore kv)
    {
        _kv = kv ?? throw new ArgumentNullException(nameof(kv));
    }

    /// <summary>Hydrate the in-memory cache from localStorage.
    /// Call once at app start (before <see cref="MobService.InitializeAsync"/>'s
    /// list-recovery loop reads). Corrupt JSON is treated as empty
    /// rather than thrown - a stale cache is recoverable; a startup
    /// crash isn't.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var json = await _kv.GetAsync(StorageKey, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var arr = JsonSerializer.Deserialize(json,
                OnaPlotter.Services.Json.OnaJsonContext.Default.ResolvedMobPositionArray);
            if (arr is null) return;
            foreach (var p in arr)
            {
                if (string.IsNullOrEmpty(p.ServerId)) continue;
                _cache[p.ServerId] = (p.Latitude, p.Longitude);
            }
        }
        catch (JsonException)
        {
            // Schema skew / corruption: drop the cache and start
            // clean. The next reconcile will repopulate.
        }
    }

    /// <summary>Read the cached position for <paramref name="serverId"/>,
    /// or false if no entry. Tuple destructured so the caller can
    /// promote it directly to <c>double?</c> lat/lon variables.</summary>
    public bool TryGet(string serverId, out double lat, out double lon)
    {
        if (_cache.TryGetValue(serverId, out var p))
        {
            lat = p.Lat;
            lon = p.Lon;
            return true;
        }
        lat = 0;
        lon = 0;
        return false;
    }

    /// <summary>Record / overwrite the position for
    /// <paramref name="serverId"/>. Persist is fire-and-forget -
    /// in-memory state is the source of truth between writes.</summary>
    public void Save(string serverId, double lat, double lon)
    {
        _cache[serverId] = (lat, lon);
        _ = PersistAsync(CancellationToken.None);
    }

    /// <summary>Drop the entry for <paramref name="serverId"/>.
    /// No-op + no persist when the entry wasn't there - avoids a
    /// spurious KV write on every clear.</summary>
    public void Forget(string serverId)
    {
        if (_cache.Remove(serverId))
        {
            _ = PersistAsync(CancellationToken.None);
        }
    }

    private Task PersistAsync(CancellationToken ct)
    {
        if (_cache.Count == 0)
        {
            return _kv.RemoveAsync(StorageKey, ct);
        }
        var arr = _cache
            .Select(kv => new ResolvedMobPosition(kv.Key, kv.Value.Lat, kv.Value.Lon))
            .ToArray();
        var json = JsonSerializer.Serialize(arr,
            OnaPlotter.Services.Json.OnaJsonContext.Default.ResolvedMobPositionArray);
        return _kv.SetAsync(StorageKey, json, ct);
    }
}

/// <summary>Persistable map entry for the resolved-position cache.
/// Written when reconcile transfers position from local synthetic
/// to server twin; consumed on <see cref="MobService.InitializeAsync"/>
/// to recover the position across a session boundary.</summary>
public sealed record ResolvedMobPosition(
    string ServerId,
    double Latitude,
    double Longitude);
