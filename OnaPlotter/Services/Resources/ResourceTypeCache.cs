using Microsoft.Extensions.Logging;

namespace OnaPlotter.Services.Resources;

/// <summary>
/// Per-resource-type cache + change-event hub. Pulled out of
/// <see cref="ResourceStore"/> because the four resource types
/// (routes / waypoints / notes / regions) all need exactly the same
/// shape: an id-keyed dictionary, a snapshot list that's cached
/// across reads and invalidated on mutation, a Changed event fired
/// after upsert, a Removed event fired after delete. Without this
/// generic primitive ResourceStore was four near-identical 30-line
/// blocks side by side.
///
/// <para><b>Threading</b>: not thread-safe. Mirrors
/// <see cref="ResourceStore"/>'s contract -- Blazor WASM is single-
/// threaded, so the lock-free read + mutate-on-event pattern is
/// fine. Snapshots handed out to consumers are safe to iterate
/// even while a mutation lands (the cached <see cref="Snapshot"/>
/// reference is dropped, but the consumer's captured list keeps
/// pointing at its time-of-read content).</para>
/// </summary>
internal sealed class ResourceTypeCache<T> where T : class
{
    private readonly Dictionary<string, T> _byId = new();
    private List<T>? _snapshot;
    private readonly ILogger _logger;
    private readonly string _typeLabel;

    /// <param name="typeLabel">Singular form ("route", "waypoint",
    /// "note", "region") used as the prefix in subscriber-throw
    /// log messages -- "[resources] route changed subscriber threw".
    /// Keep it lowercase to match the surrounding log style.</param>
    public ResourceTypeCache(string typeLabel, ILogger logger)
    {
        _typeLabel = typeLabel;
        _logger = logger;
    }

    /// <summary>Cached snapshot of all entries currently in the cache.
    /// Same identity reference returned across reads until something
    /// mutates the dictionary. See the matching docs on
    /// <see cref="ResourceStore.Routes"/> for the safety contract.</summary>
    public IReadOnlyList<T> Snapshot
    {
        get
        {
            if (_snapshot is { } cached) return cached;
            var s = new List<T>(_byId.Count);
            s.AddRange(_byId.Values);
            _snapshot = s;
            return s;
        }
    }

    public int Count => _byId.Count;

    public T? Get(string id) =>
        _byId.TryGetValue(id, out var v) ? v : null;

    /// <summary>Fires when an entry is created or updated.</summary>
    public event Action<string>? Changed;

    /// <summary>Fires AFTER the entry is dropped from the cache, so a
    /// handler that re-checks via <see cref="Get"/> sees null.</summary>
    public event Action<string>? Removed;

    /// <summary>Upsert <paramref name="entry"/> under <paramref name="id"/>.
    /// Always invalidates the snapshot and fires Changed; callers that
    /// need an "is this actually new vs an update" distinction should
    /// look at <see cref="Replace"/> which tracks added / updated /
    /// removed counts.</summary>
    public void Apply(string id, T entry)
    {
        _byId[id] = entry;
        _snapshot = null;
        Fire(Changed, id, "changed");
    }

    /// <summary>Drop the entry. Returns true if the cache had it (so
    /// the caller can short-circuit downstream work for unknown ids).</summary>
    public bool Remove(string id)
    {
        if (!_byId.Remove(id)) return false;
        _snapshot = null;
        Fire(Removed, id, "removed");
        return true;
    }

    /// <summary>Reconcile the cache against a fresh server snapshot.
    /// Upserts every entry whose id selector returns non-empty, fires
    /// Changed for each. Then walks the dictionary to find ids absent
    /// from <paramref name="fresh"/>, removes them, fires Removed.
    /// Returns the per-bucket count breakdown so the caller can log a
    /// "+A/~U/-R" reconcile summary.
    /// <paramref name="scratch"/> is a HashSet borrowed from the caller
    /// (cleared on entry) so the membership-check allocation can be
    /// reused across multiple Replace calls in the same reconcile pass.</summary>
    public ResourceTypeCache<T>.ReplaceCounts Replace(
        IEnumerable<T> fresh,
        Func<T, string?> idSelector,
        HashSet<string> scratch)
    {
        scratch.Clear();
        int added = 0, updated = 0;
        bool mutated = false;
        foreach (var entry in fresh)
        {
            var id = idSelector(entry);
            if (string.IsNullOrEmpty(id)) continue;
            scratch.Add(id);
            if (_byId.ContainsKey(id)) updated++; else added++;
            _byId[id] = entry;
            mutated = true;
            Fire(Changed, id, "changed");
        }
        // Stale-id detection: lazy-allocate only when there's
        // actually something to remove (no-deletes is the common
        // reconcile path).
        List<string>? stale = null;
        foreach (var k in _byId.Keys)
        {
            if (!scratch.Contains(k)) (stale ??= []).Add(k);
        }
        int removed = 0;
        if (stale is not null)
        {
            foreach (var id in stale)
            {
                _byId.Remove(id);
                Fire(Removed, id, "removed");
            }
            removed = stale.Count;
            mutated = true;
        }
        if (mutated) _snapshot = null;
        return new ReplaceCounts(added, updated, removed);
    }

    private void Fire(Action<string>? handler, string id, string action)
    {
        if (handler is null) return;
        try { handler.Invoke(id); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[resources] {Type} {Action} subscriber threw",
                _typeLabel, action);
        }
    }

    /// <summary>Per-type reconcile breakdown. "added" = id wasn't in
    /// the cache before the reconcile; "updated" = id was already
    /// there (reference replaced); "removed" = id was in the cache
    /// but not in the fresh list.</summary>
    public readonly record struct ReplaceCounts(int Added, int Updated, int Removed);
}
