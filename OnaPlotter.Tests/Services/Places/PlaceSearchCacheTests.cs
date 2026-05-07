using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services;
using OnaPlotter.Services.Places;

namespace OnaPlotter.Tests.Services.Places;

/// <summary>
/// Pins cache hit / miss / FIFO eviction. localStorage is faked
/// with an in-memory dictionary; the cache layer is then exercised
/// directly with PutAsync / TryGetAsync / ClearAsync.
/// </summary>
public class PlaceSearchCacheTests
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

    private static (PlaceSearchCache cache, InMemoryKv kv) NewCache()
    {
        var kv = new InMemoryKv();
        var cache = new PlaceSearchCache(kv, NullLogger<PlaceSearchCache>.Instance);
        return (cache, kv);
    }

    private static PlaceResult Pl(string name, double lat = 0, double lon = 0) =>
        new(name, name, lat, lon, "photon");

    [Test]
    public async Task Empty_Cache_Returns_Null_On_Miss()
    {
        var (cache, _) = NewCache();
        await Assert.That(await cache.TryGetAsync("anything")).IsNull();
    }

    [Test]
    public async Task Put_Then_Get_Roundtrips()
    {
        var (cache, _) = NewCache();
        await cache.PutAsync("Berlin", new[] { Pl("Berlin", 52.5, 13.4) });

        var hit = await cache.TryGetAsync("Berlin");
        await Assert.That(hit).IsNotNull();
        await Assert.That(hit!.Count).IsEqualTo(1);
        await Assert.That(hit[0].Name).IsEqualTo("Berlin");
        await Assert.That(hit[0].Lat).IsEqualTo(52.5);
    }

    [Test]
    public async Task Lookup_Is_Case_Insensitive_And_Trim_Tolerant()
    {
        var (cache, _) = NewCache();
        await cache.PutAsync("Fowl Cay", new[] { Pl("Fowl Cay") });

        await Assert.That(await cache.TryGetAsync("fowl cay")).IsNotNull();
        await Assert.That(await cache.TryGetAsync("FOWL CAY")).IsNotNull();
        await Assert.That(await cache.TryGetAsync("  Fowl Cay  ")).IsNotNull();
    }

    [Test]
    public async Task Empty_Results_Are_Not_Cached()
    {
        // An empty hit (provider couldn't match a partial word) shouldn't
        // pin "no results" - the helm finishing the word should re-query
        // live, not return an empty cache entry.
        var (cache, kv) = NewCache();
        await cache.PutAsync("foo", Array.Empty<PlaceResult>());

        await Assert.That(await cache.TryGetAsync("foo")).IsNull();
        await Assert.That(kv.Store.ContainsKey(PlaceSearchCache.StorageKey)).IsFalse();
    }

    [Test]
    public async Task Repeat_Put_Replaces_Same_Key()
    {
        var (cache, _) = NewCache();
        await cache.PutAsync("Berlin", new[] { Pl("Berlin v1") });
        await cache.PutAsync("Berlin", new[] { Pl("Berlin v2") });

        var hit = await cache.TryGetAsync("Berlin");
        await Assert.That(hit!.Count).IsEqualTo(1);
        await Assert.That(hit[0].Name).IsEqualTo("Berlin v2");
    }

    [Test]
    public async Task Eviction_Drops_Oldest_When_Cap_Exceeded()
    {
        var (cache, _) = NewCache();
        // Insert MaxEntries + 5 distinct queries; the first 5 should
        // be gone after the 25th insertion.
        for (int i = 0; i < PlaceSearchCache.MaxEntries + 5; i++)
        {
            await cache.PutAsync($"q{i}", new[] { Pl($"q{i}") });
        }

        // The 5 oldest were evicted: q0..q4 should miss.
        for (int i = 0; i < 5; i++)
        {
            await Assert.That(await cache.TryGetAsync($"q{i}")).IsNull();
        }
        // q5..q24 should still hit.
        for (int i = 5; i < PlaceSearchCache.MaxEntries + 5; i++)
        {
            await Assert.That(await cache.TryGetAsync($"q{i}")).IsNotNull();
        }
    }

    [Test]
    public async Task Persistence_Survives_New_Cache_Instance()
    {
        var kv = new InMemoryKv();
        var cache1 = new PlaceSearchCache(kv, NullLogger<PlaceSearchCache>.Instance);
        await cache1.PutAsync("Marathon", new[] { Pl("Marathon", 24.7, -81.1) });

        // A second instance reading the same kv (= browser reload + same
        // localStorage) should see the entry.
        var cache2 = new PlaceSearchCache(kv, NullLogger<PlaceSearchCache>.Instance);
        var hit = await cache2.TryGetAsync("Marathon");
        await Assert.That(hit).IsNotNull();
        await Assert.That(hit![0].Lat).IsEqualTo(24.7);
    }

    [Test]
    public async Task ClearAsync_Wipes_Storage()
    {
        var (cache, kv) = NewCache();
        await cache.PutAsync("Berlin", new[] { Pl("Berlin") });
        await cache.ClearAsync();

        await Assert.That(await cache.TryGetAsync("Berlin")).IsNull();
        await Assert.That(kv.Store.ContainsKey(PlaceSearchCache.StorageKey)).IsFalse();
    }

    [Test]
    public async Task Corrupt_Storage_Degrades_To_Empty()
    {
        // localStorage tampering / schema skew shouldn't take down the
        // cache. Hydrate-time JsonException is swallowed; subsequent
        // PutAsync rebuilds the storage cleanly.
        var kv = new InMemoryKv();
        kv.Store[PlaceSearchCache.StorageKey] = "{not valid json";
        var cache = new PlaceSearchCache(kv, NullLogger<PlaceSearchCache>.Instance);

        await Assert.That(await cache.TryGetAsync("anything")).IsNull();

        // Subsequent put writes valid JSON.
        await cache.PutAsync("Berlin", new[] { Pl("Berlin") });
        await Assert.That(await cache.TryGetAsync("Berlin")).IsNotNull();
        // The corrupt entry was overwritten with valid JSON.
        await Assert.That(kv.Store[PlaceSearchCache.StorageKey].StartsWith('['))
            .IsTrue();
    }
}
