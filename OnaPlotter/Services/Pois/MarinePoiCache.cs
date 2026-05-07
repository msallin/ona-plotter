using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Json;

namespace OnaPlotter.Services.Pois;

/// <summary>
/// Persistent id-keyed cache of marine POIs ever seen by the client.
/// Backed by <see cref="IKeyValueStore"/> (localStorage in production).
/// One flat dictionary, not a per-bbox or per-category cache: the
/// data shape (a few thousand POIs total at the upper end of a
/// season's worth of cruising) fits comfortably and the simpler
/// model removes "did this fetch cover that bbox" bookkeeping that
/// nothing else benefits from.
///
/// <para><b>Offline shape</b> (the headline feature). When the
/// helm sails into a marina they visited last week, the chart shows
/// the cached fuel dock / pump-out / chandlery markers immediately,
/// even with no internet. <see cref="OverpassPoiService"/> may return
/// an empty list on the failed online fetch; the controller still
/// renders <see cref="QueryAsync"/>'s view of the cache so the helm
/// doesn't lose what they had.</para>
///
/// <para><b>Eviction</b> is FIFO by <see cref="MarinePoi.LastSeenUtc"/>.
/// Once over <see cref="MaxEntries"/>, oldest entries drop. A POI
/// re-seen during a fresh fetch has its LastSeenUtc bumped, so
/// frequently-visited regions stay sticky.</para>
///
/// <para><b>Persistence</b> is debounced. Each <see cref="MergeAsync"/>
/// updates the in-memory dict immediately and resets a
/// <see cref="PersistDebounce"/> timer; if no further merge lands
/// inside the window, we flush to localStorage. A burst of
/// pan-driven fetches therefore collapses to a single ~1.25 MB
/// write at end-of-burst rather than one per fetch. The renderer
/// thread doesn't see a hitch on every moveend tick; the helm
/// loses at most one debounce window of merges if the tab closes
/// before the timer fires (acceptable - POIs refetch on next
/// visit). <see cref="DisposeAsync"/> flushes any pending writes.</para>
/// </summary>
public sealed class MarinePoiCache : IAsyncDisposable
{
    public const string StorageKey = "marinePoi.cache.v1";

    /// <summary>Cap on the cached entry count. Sized for ~1.25 MB on
    /// disk after JSON encoding, which keeps the localStorage
    /// quota comfortable while covering several seasons of regular
    /// haunts. Helms with denser cruising areas will see FIFO
    /// eviction kick in; the persistent ones stay sticky as long as
    /// the helm keeps visiting them (LastSeenUtc bumps on re-fetch).</summary>
    public const int MaxEntries = 5000;

    /// <summary>Quiet-period before a pending write actually hits
    /// localStorage. Each merge resets the timer; the persist runs
    /// only after merges stop landing for this long. 2 s is short
    /// enough that a tab-close mid-session loses very little, long
    /// enough to coalesce a pan-driven fetch burst (typical helm
    /// stops panning for &gt; 2 s when they want to look at
    /// something).</summary>
    public static readonly TimeSpan PersistDebounce = TimeSpan.FromSeconds(2);

    private readonly IKeyValueStore _kv;
    private readonly ILogger<MarinePoiCache> _logger;
    private Dictionary<string, MarinePoi>? _entries;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Debounce state. _persistCts cancels a pending flush when a new
    // merge lands; _pendingPersist tracks the in-flight delay task so
    // DisposeAsync can await the final flush. _dirty signals "memory
    // is ahead of disk" - consulted on dispose to decide whether a
    // forced flush is needed even when no debounce is pending.
    private CancellationTokenSource? _persistCts;
    private Task? _pendingPersist;
    private bool _dirty;
    private bool _disposed;

    public MarinePoiCache(IKeyValueStore kv, ILogger<MarinePoiCache> logger)
    {
        _kv = kv;
        _logger = logger;
    }

    /// <summary>
    /// Insert / update each POI by id. Bumps <see cref="MarinePoi.LastSeenUtc"/>
    /// on existing entries so re-seeing a POI during a fresh fetch
    /// keeps it from FIFO eviction. Persists to localStorage once at
    /// the end of the merge.
    /// </summary>
    public async Task MergeAsync(IReadOnlyList<MarinePoi> incoming, CancellationToken ct = default)
    {
        if (incoming.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            var entries = await EnsureLoadedLockedAsync(ct);
            foreach (var poi in incoming)
            {
                entries[poi.Id] = poi;
            }
            TrimToMaxEntriesLocked(entries);
            // Mark dirty + (re-)arm the debounced flush. The actual
            // localStorage write happens only after PersistDebounce of
            // quiet, so a pan-driven fetch burst collapses to one write.
            _dirty = true;
            ArmPersistDebounceLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Return cached POIs whose coordinates intersect the bbox AND
    /// whose category is in the enabled set. Used by the controller
    /// on every render - both after a fresh fetch (to render the
    /// merged union) and on a cache-only render (offline / before
    /// first fetch). Returns an empty list when no categories are
    /// enabled or the cache is empty.
    /// </summary>
    public async Task<IReadOnlyList<MarinePoi>> QueryAsync(
        double south, double west, double north, double east,
        IReadOnlySet<MarinePoiCategory> categories,
        CancellationToken ct = default)
    {
        if (categories.Count == 0) return [];
        await _gate.WaitAsync(ct);
        try
        {
            var entries = await EnsureLoadedLockedAsync(ct);
            if (entries.Count == 0) return [];
            var hits = new List<MarinePoi>();
            foreach (var poi in entries.Values)
            {
                if (!categories.Contains(poi.Category)) continue;
                if (poi.Lat < south || poi.Lat > north) continue;
                if (poi.Lon < west || poi.Lon > east) continue;
                hits.Add(poi);
            }
            return hits;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Wipe the cache. Future Settings affordance and tests.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _entries = [];
            // Cancel any pending debounced write so it doesn't replay
            // the just-cleared in-memory snapshot back onto disk.
            _persistCts?.Cancel();
            _dirty = false;
            await _kv.RemoveAsync(StorageKey, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Flush any pending debounced write. Called by tests + on
    /// page-tear-down via <see cref="DisposeAsync"/> so a tab close
    /// inside the debounce window doesn't drop the merge.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Cancel any pending background flush so we don't double-write.
            _persistCts?.Cancel();
            if (!_dirty || _entries is null) return;
            await PersistLockedAsync(_entries, ct);
            _dirty = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await FlushAsync(); }
        catch { /* tab teardown - best-effort flush, never throw out of dispose */ }
        _persistCts?.Cancel();
        _persistCts?.Dispose();
        _persistCts = null;
    }

    /// <summary>Test seam: snapshot of what's currently held. Returns
    /// a copy so the test can mutate freely without racing the live
    /// dictionary.</summary>
    internal async Task<IReadOnlyDictionary<string, MarinePoi>> SnapshotAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var entries = await EnsureLoadedLockedAsync(ct);
            return new Dictionary<string, MarinePoi>(entries);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, MarinePoi>> EnsureLoadedLockedAsync(CancellationToken ct)
    {
        if (_entries is not null) return _entries;
        try
        {
            var raw = await _kv.GetAsync(StorageKey, ct);
            if (string.IsNullOrWhiteSpace(raw)) { _entries = []; return _entries; }
            var loaded = JsonSerializer.Deserialize(raw, OnaJsonContext.Default.MarinePoiArray);
            _entries = new Dictionary<string, MarinePoi>(StringComparer.Ordinal);
            if (loaded is null) return _entries;
            foreach (var poi in loaded)
            {
                if (poi is null) continue;
                if (string.IsNullOrWhiteSpace(poi.Id)) continue;
                if (!double.IsFinite(poi.Lat) || !double.IsFinite(poi.Lon)) continue;
                if (poi.Lat < -90 || poi.Lat > 90 || poi.Lon < -180 || poi.Lon > 180) continue;
                _entries[poi.Id] = poi;
            }
            return _entries;
        }
        catch (Exception ex) when (ex is Microsoft.JSInterop.JSException
                                     or System.Text.Json.JsonException)
        {
            // Same shape as PlaceSearchCache: a corrupted entry / a
            // browser without storage degrades to "empty cache" rather
            // than throwing into the controller.
            _logger.LogWarning(ex, "[marine-poi-cache] hydrate skipped");
            _entries = [];
            return _entries;
        }
    }

    /// <summary>
    /// Reset the debounce window. Cancels any in-flight delay and
    /// schedules a fresh one; if no further merge lands inside the
    /// window, the trailing-edge callback flushes the in-memory dict
    /// to localStorage. Caller must hold <see cref="_gate"/>.
    /// <para>The flush itself reacquires the gate (it runs on a
    /// continuation, after the merge has long since released the
    /// lock), so a merge in the middle of the flush serialises
    /// behind it.</para>
    /// </summary>
    private void ArmPersistDebounceLocked()
    {
        if (_disposed) return;
        _persistCts?.Cancel();
        _persistCts?.Dispose();
        _persistCts = new CancellationTokenSource();
        var ct = _persistCts.Token;
        _pendingPersist = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PersistDebounce, ct);
            }
            catch (OperationCanceledException) { return; }
            // Re-acquire the gate to read a consistent snapshot + persist.
            try
            {
                await _gate.WaitAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            try
            {
                if (ct.IsCancellationRequested) return;
                if (!_dirty || _entries is null) return;
                await PersistLockedAsync(_entries, ct);
                _dirty = false;
            }
            finally
            {
                _gate.Release();
            }
        }, ct);
    }

    private static void TrimToMaxEntriesLocked(Dictionary<string, MarinePoi> entries)
    {
        if (entries.Count <= MaxEntries) return;
        // Sort by LastSeenUtc ascending, drop the oldest until we're
        // back at the cap. O(n log n) once per merge that overflows -
        // acceptable for n <= MaxEntries + a fetch's worth of new
        // entries.
        var ordered = entries.OrderBy(kv => kv.Value.LastSeenUtc).ToList();
        int toRemove = entries.Count - MaxEntries;
        for (int i = 0; i < toRemove; i++)
        {
            entries.Remove(ordered[i].Key);
        }
    }

    private async Task PersistLockedAsync(Dictionary<string, MarinePoi> entries, CancellationToken ct)
    {
        try
        {
            var array = entries.Values.ToArray();
            var json = JsonSerializer.Serialize(array, OnaJsonContext.Default.MarinePoiArray);
            await _kv.SetAsync(StorageKey, json, ct);
        }
        catch (Exception ex) when (ex is Microsoft.JSInterop.JSException
                                     or System.Text.Json.JsonException)
        {
            // QuotaExceededError / private-mode Safari / page-teardown
            // race: skip the persist, in-memory cache stays one merge
            // ahead of disk until the next successful write.
            _logger.LogWarning(ex, "[marine-poi-cache] persist skipped");
        }
    }
}
