using System.Text.Json;
using OnaPlotter.Services.Json;

namespace OnaPlotter.Services.Places;

/// <summary>
/// LRU-ish (FIFO eviction) cache of geocoder results, persisted via
/// <see cref="IKeyValueStore"/> (localStorage in production). The
/// helm searches the same handful of harbours / capes / rivers
/// across a season; caching avoids redundant Photon round-trips
/// when the helm reopens the search box on a familiar query.
///
/// <para>Why FIFO over true LRU: simpler, and at <c>MaxEntries=20</c>
/// the difference is irrelevant. The "most recent 20" is good enough;
/// promoting on cache-hit would require a write on every hit, and
/// the localStorage write cost dwarfs the FIFO miss-rate cost.</para>
/// <para>Storage shape: a single JSON array under
/// <see cref="StorageKey"/>. Bumping the key version to
/// <c>places.cache.v2</c> is the migration path if the entry shape
/// ever changes; v1 entries on the helm's localStorage become
/// orphans we ignore-then-overwrite.</para>
/// </summary>
public sealed class PlaceSearchCache
{
    public const string StorageKey = "places.cache.v1";

    /// <summary>Cap on cached queries. 20 covers a typical season's
    /// worth of regular-haunts plus a long tail of one-offs without
    /// inflating localStorage. The localStorage quota is per-origin
    /// at 5-10 MB depending on browser; one query (10 results, ~150
    /// bytes each) is roughly 1.5 KB, so 20 entries is &lt; 30 KB.</summary>
    public const int MaxEntries = 20;

    private readonly IKeyValueStore _kv;
    private readonly ILogger<PlaceSearchCache> _logger;

    /// <summary>In-memory shadow of the persisted list. Populated on
    /// first access, then mutated in place; persisted on every
    /// change. Avoids re-reading + re-parsing the whole list on
    /// each lookup.</summary>
    private List<CachedQuery>? _entries;

    public PlaceSearchCache(IKeyValueStore kv, ILogger<PlaceSearchCache> logger)
    {
        _kv = kv;
        _logger = logger;
    }

    /// <summary>Lookup a query (case-insensitive on the cache key).
    /// Returns null on miss; the caller falls through to the live
    /// provider and writes back via <see cref="PutAsync"/>.</summary>
    public async Task<IReadOnlyList<PlaceResult>?> TryGetAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var entries = await EnsureLoadedAsync(ct);
        var key = NormaliseKey(query);
        foreach (var entry in entries)
        {
            if (entry.Query == key) return entry.Results;
        }
        return null;
    }

    /// <summary>Cache the (query, results) pair. Newest first, FIFO-
    /// trim past <see cref="MaxEntries"/>. No-op when results are
    /// empty: an empty hit usually means the helm typed a partial
    /// word the provider couldn't match, and caching the miss would
    /// keep returning empty even after the user finishes the
    /// word.</summary>
    public async Task PutAsync(string query, IReadOnlyList<PlaceResult> results, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        if (results.Count == 0) return;
        var entries = await EnsureLoadedAsync(ct);
        var key = NormaliseKey(query);

        // Drop any existing entry for the same key (newest-first
        // policy: the new write displaces the old position).
        entries.RemoveAll(e => e.Query == key);

        entries.Insert(0, new CachedQuery(key, [.. results]));
        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }
        await PersistAsync(entries, ct);
    }

    /// <summary>Wipe the cache. Used by tests + a future "clear
    /// cache" affordance on Settings.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        _entries = [];
        await _kv.RemoveAsync(StorageKey, ct);
    }

    private async Task<List<CachedQuery>> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_entries is not null) return _entries;
        try
        {
            var raw = await _kv.GetAsync(StorageKey, ct);
            if (string.IsNullOrWhiteSpace(raw)) { _entries = []; return _entries; }
            var loaded = JsonSerializer.Deserialize(raw, OnaJsonContext.Default.CachedQueryArray);
            _entries = loaded is null
                ? []
                : new List<CachedQuery>(loaded);
            return _entries;
        }
        catch (JsonException ex)
        {
            // Corrupted localStorage entry (browser-extension tampering
            // or a v0 -> v1 schema skew that pre-dates this key). Treat
            // as empty; the next PutAsync overwrites cleanly.
            _logger.LogWarning("[place-cache] hydrate skipped: {Message}", ex.Message);
            _entries = [];
            return _entries;
        }
    }

    private async Task PersistAsync(List<CachedQuery> entries, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(entries.ToArray(), OnaJsonContext.Default.CachedQueryArray);
            await _kv.SetAsync(StorageKey, json, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("[place-cache] persist skipped: {Message}", ex.Message);
        }
    }

    /// <summary>Lower-case + trim so "Fowl Cay" / "fowl cay" hit
    /// the same cache slot. Internal so the test fixture can
    /// assert against the same canonicalised form.</summary>
    internal static string NormaliseKey(string query) => query.Trim().ToLowerInvariant();
}

/// <summary>
/// One cache entry: the canonical-form query + the provider results
/// it returned. Persisted as a JSON array of these. Public because
/// the source-gen JSON context needs to see it.
/// </summary>
public sealed record CachedQuery(string Query, PlaceResult[] Results);
