using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Pois;

namespace OnaPlotter.Tests.Services.Pois;

/// <summary>
/// Cache contract: id-keyed merge, bbox + category query, FIFO trim,
/// hydrate-from-storage round-trip. localStorage is faked with an
/// in-memory dictionary so the cache logic can be exercised without
/// JS interop.
/// </summary>
public class MarinePoiCacheTests
{
    private sealed class InMemoryKv : IKeyValueStore
    {
        public Dictionary<string, string> Store { get; } = [];
        public Task<string?> GetAsync(string k, CancellationToken ct = default)
            => Task.FromResult(Store.TryGetValue(k, out var v) ? v : null);
        public Task SetAsync(string k, string v, CancellationToken ct = default)
        { Store[k] = v; return Task.CompletedTask; }
        public Task RemoveAsync(string k, CancellationToken ct = default)
        { Store.Remove(k); return Task.CompletedTask; }
    }

    private static MarinePoi P(string id, double lat, double lon,
        MarinePoiCategory cat = MarinePoiCategory.Marina,
        DateTime? lastSeen = null)
        => new(
            Id: id,
            Lat: lat,
            Lon: lon,
            Category: cat,
            Name: id,
            Tags: new Dictionary<string, string>(),
            LastSeenUtc: lastSeen ?? new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc));

    private static IReadOnlySet<MarinePoiCategory> Cat(params MarinePoiCategory[] cats)
        => new HashSet<MarinePoiCategory>(cats);

    private static (MarinePoiCache cache, InMemoryKv kv) NewCache()
    {
        var kv = new InMemoryKv();
        return (new MarinePoiCache(kv, NullLogger<MarinePoiCache>.Instance), kv);
    }

    [Test]
    public async Task Empty_Query_Returns_Empty()
    {
        var (cache, _) = NewCache();
        var hits = await cache.QueryAsync(0, 0, 90, 90, Cat(MarinePoiCategory.Marina));
        await Assert.That(hits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Merge_Then_Query_Returns_Inside_Bbox()
    {
        var (cache, _) = NewCache();
        await cache.MergeAsync(new[]
        {
            P("n1", 40.5, -73.5, MarinePoiCategory.Marina),
            P("n2", 50.0, 10.0, MarinePoiCategory.Marina),  // outside the test bbox
        });

        var hits = await cache.QueryAsync(40, -74, 41, -73, Cat(MarinePoiCategory.Marina));
        await Assert.That(hits.Count).IsEqualTo(1);
        await Assert.That(hits[0].Id).IsEqualTo("n1");
    }

    [Test]
    public async Task Query_FiltersByCategory()
    {
        // Cache holds two POIs in the same bbox but different
        // categories; query mask only includes one.
        var (cache, _) = NewCache();
        await cache.MergeAsync(new[]
        {
            P("n1", 40.5, -73.5, MarinePoiCategory.Marina),
            P("n2", 40.6, -73.6, MarinePoiCategory.Fuel),
        });

        var hits = await cache.QueryAsync(40, -74, 41, -73, Cat(MarinePoiCategory.Marina));
        await Assert.That(hits.Count).IsEqualTo(1);
        await Assert.That(hits[0].Category).IsEqualTo(MarinePoiCategory.Marina);
    }

    [Test]
    public async Task Query_NoCategories_Returns_Empty()
    {
        // Master gate: zero categories enabled = nothing to draw.
        var (cache, _) = NewCache();
        await cache.MergeAsync(new[] { P("n1", 40.5, -73.5) });
        var hits = await cache.QueryAsync(40, -74, 41, -73, Cat());
        await Assert.That(hits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Merge_BumpsLastSeen_OnRevisit()
    {
        // Re-seeing a POI in a fresh fetch must bump LastSeenUtc;
        // otherwise FIFO eviction would drop popular haunts that
        // happen to have been first-seen long ago.
        var (cache, _) = NewCache();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 5, 7, 0, 0, 0, DateTimeKind.Utc);
        await cache.MergeAsync(new[] { P("n1", 40.5, -73.5, lastSeen: t1) });
        await cache.MergeAsync(new[] { P("n1", 40.5, -73.5, lastSeen: t2) });

        var snap = await cache.SnapshotAsync();
        await Assert.That(snap["n1"].LastSeenUtc).IsEqualTo(t2);
    }

    [Test]
    public async Task Persist_Then_Reload_Roundtrips()
    {
        // Cache is the offline lifeline. A fresh instance reading the
        // same KV store must recover the prior fetch's POIs. Persist
        // is debounced, so the test forces a flush before "reloading"
        // -- production gets the same flush from DisposeAsync on tab
        // close (best-effort) or on the next merge after the debounce
        // window elapses.
        var kv = new InMemoryKv();
        {
            var cache = new MarinePoiCache(kv, NullLogger<MarinePoiCache>.Instance);
            await cache.MergeAsync(new[]
            {
                P("n1", 40.5, -73.5, MarinePoiCategory.Marina),
                P("w2", 41.0, -73.0, MarinePoiCategory.Fuel),
            });
            await cache.FlushAsync();
        }
        // Second instance: simulates a page reload.
        var fresh = new MarinePoiCache(kv, NullLogger<MarinePoiCache>.Instance);
        var hits = await fresh.QueryAsync(40, -74, 42, -73, Cat(
            MarinePoiCategory.Marina, MarinePoiCategory.Fuel));
        await Assert.That(hits.Count).IsEqualTo(2);
    }

    [Test]
    public async Task FifoTrim_DropsOldest()
    {
        // Force-evict by writing more than MaxEntries with distinct
        // LastSeenUtc; the oldest must be the casualty. Tested by
        // crafting a tiny synthetic overflow; production Cap is much
        // larger but the algorithm is the same.
        // Strategy: write exactly MaxEntries+1 entries, distinct ids
        // and ascending timestamps. The earliest-stamped one drops.
        var (cache, _) = NewCache();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var batch = new List<MarinePoi>();
        for (int i = 0; i <= MarinePoiCache.MaxEntries; i++)
        {
            batch.Add(P($"n{i}", 40.0, -73.5, lastSeen: t0.AddSeconds(i)));
        }
        await cache.MergeAsync(batch);

        var snap = await cache.SnapshotAsync();
        await Assert.That(snap.Count).IsEqualTo(MarinePoiCache.MaxEntries);
        // n0 had the smallest timestamp; it must be the one evicted.
        await Assert.That(snap.ContainsKey("n0")).IsFalse();
        await Assert.That(snap.ContainsKey($"n{MarinePoiCache.MaxEntries}")).IsTrue();
    }

    [Test]
    public async Task Clear_RemovesEverything_AndStorageKey()
    {
        var (cache, kv) = NewCache();
        await cache.MergeAsync(new[] { P("n1", 40.5, -73.5) });
        await cache.FlushAsync();  // debounce-bypass for the test
        await Assert.That(kv.Store.ContainsKey(MarinePoiCache.StorageKey)).IsTrue();

        await cache.ClearAsync();
        await Assert.That(kv.Store.ContainsKey(MarinePoiCache.StorageKey)).IsFalse();

        var hits = await cache.QueryAsync(0, 0, 90, 90, Cat(MarinePoiCategory.Marina));
        await Assert.That(hits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MergeAsync_Debounces_LocalStorageWrite()
    {
        // Persist is debounced -- merging shouldn't punch through to
        // localStorage on every call. Memory must be up-to-date
        // immediately (queries hit memory, not disk), but the KV
        // write only lands when the helm asks for it via
        // FlushAsync (or when the debounce timer fires; not exercised
        // here to keep the test fast).
        var kv = new CountingKv();
        var cache = new MarinePoiCache(kv, NullLogger<MarinePoiCache>.Instance);
        await cache.MergeAsync(new[] { P("n1", 40.5, -73.5) });
        await cache.MergeAsync(new[] { P("n2", 41.0, -73.0) });
        await cache.MergeAsync(new[] { P("n3", 41.5, -73.5) });

        // Memory has all three already.
        var snap = await cache.SnapshotAsync();
        await Assert.That(snap.Count).IsEqualTo(3);

        // Disk hasn't been touched yet -- debounce is still pending.
        await Assert.That(kv.SetCalls).IsEqualTo(0);

        // Forced flush coalesces all three merges into one write.
        await cache.FlushAsync();
        await Assert.That(kv.SetCalls).IsEqualTo(1);
    }

    [Test]
    public async Task FlushAsync_NoOpWhenNoDirtyData()
    {
        // Calling Flush without any prior merge shouldn't write.
        var kv = new CountingKv();
        var cache = new MarinePoiCache(kv, NullLogger<MarinePoiCache>.Instance);
        await cache.FlushAsync();
        await Assert.That(kv.SetCalls).IsEqualTo(0);
    }

    [Test]
    public async Task DisposeAsync_FlushesPendingWrites()
    {
        // Tab-close path: the debounce hasn't elapsed but the helm's
        // about to walk away. Dispose must persist what's in memory
        // so the next session sees it.
        var kv = new CountingKv();
        var cache = new MarinePoiCache(kv, NullLogger<MarinePoiCache>.Instance);
        await cache.MergeAsync(new[] { P("n1", 40.5, -73.5) });
        await Assert.That(kv.SetCalls).IsEqualTo(0);

        await cache.DisposeAsync();
        await Assert.That(kv.SetCalls).IsEqualTo(1);
    }

    [Test]
    public async Task ClearAsync_CancelsPendingDebouncedWrite()
    {
        // Dirty state + Clear -> the cancelled debounce mustn't
        // resurrect the pre-clear data on disk afterwards.
        var kv = new CountingKv();
        var cache = new MarinePoiCache(kv, NullLogger<MarinePoiCache>.Instance);
        await cache.MergeAsync(new[] { P("n1", 40.5, -73.5) });
        await cache.ClearAsync();
        // ClearAsync uses RemoveAsync, not SetAsync, so SetCalls=0.
        await Assert.That(kv.SetCalls).IsEqualTo(0);

        // A subsequent Flush is a no-op (clear cleaned up dirty flag).
        await cache.FlushAsync();
        await Assert.That(kv.SetCalls).IsEqualTo(0);
    }

    /// <summary>
    /// Counts SetAsync invocations so the debounce tests can assert
    /// "memory updated, disk untouched". Otherwise behaves like
    /// <see cref="InMemoryKv"/>.
    /// </summary>
    private sealed class CountingKv : IKeyValueStore
    {
        public Dictionary<string, string> Store { get; } = [];
        public int SetCalls { get; private set; }
        public Task<string?> GetAsync(string k, CancellationToken ct = default)
            => Task.FromResult(Store.TryGetValue(k, out var v) ? v : null);
        public Task SetAsync(string k, string v, CancellationToken ct = default)
        {
            SetCalls++;
            Store[k] = v;
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string k, CancellationToken ct = default)
        { Store.Remove(k); return Task.CompletedTask; }
    }

    [Test]
    public async Task Hydrate_DropsCorruptCoords()
    {
        // Defence-in-depth: a hand-tampered localStorage must not feed
        // NaN coordinates into the JS layer (where they become
        // L.marker(NaN, NaN) and break Leaflet rendering).
        var kv = new InMemoryKv();
        // Inject a doctored payload directly: one good, one with NaN
        // lat (which JSON serialises as the literal "NaN" -- which
        // System.Text.Json refuses to parse, so we use null instead
        // and rely on the deserialiser dropping the row before our
        // sanitiser ever sees it). Simulate the "out-of-range lat"
        // path which IS deserialisable.
        kv.Store[MarinePoiCache.StorageKey] = """
            [
              {"Id":"n1","Lat":40.5,"Lon":-73.5,"Category":1,"Name":"good","tags":{},"LastSeenUtc":"2026-05-07T12:00:00Z"},
              {"Id":"n2","Lat":200.0,"Lon":-73.5,"Category":1,"Name":"bad","tags":{},"LastSeenUtc":"2026-05-07T12:00:00Z"}
            ]
            """;
        var cache = new MarinePoiCache(kv, NullLogger<MarinePoiCache>.Instance);
        var snap = await cache.SnapshotAsync();
        await Assert.That(snap.Count).IsEqualTo(1);
        await Assert.That(snap.ContainsKey("n1")).IsTrue();
    }
}
