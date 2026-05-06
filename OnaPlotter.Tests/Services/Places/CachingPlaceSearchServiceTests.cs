using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services;
using OnaPlotter.Services.Places;

namespace OnaPlotter.Tests.Services.Places;

/// <summary>
/// Pins the cache-then-fetch decorator: cache hit short-circuits
/// the inner provider, cache miss calls inner + populates, empty
/// queries are no-ops.
/// </summary>
public class CachingPlaceSearchServiceTests
{
    private sealed class InMemoryKv : IKeyValueStore
    {
        private readonly Dictionary<string, string> _s = [];
        public Task<string?> GetAsync(string k, CancellationToken ct = default)
            => Task.FromResult(_s.TryGetValue(k, out var v) ? v : null);
        public Task SetAsync(string k, string v, CancellationToken ct = default)
        { _s[k] = v; return Task.CompletedTask; }
        public Task RemoveAsync(string k, CancellationToken ct = default)
        { _s.Remove(k); return Task.CompletedTask; }
    }

    private sealed class CountingInner : IPlaceSearchService
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<PlaceResult> NextResults { get; set; } = [];
        public Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(NextResults);
        }
    }

    private static (CachingPlaceSearchService svc, CountingInner inner, PlaceSearchCache cache) New()
    {
        var kv = new InMemoryKv();
        var cache = new PlaceSearchCache(kv, NullLogger<PlaceSearchCache>.Instance);
        var inner = new CountingInner();
        var svc = new CachingPlaceSearchService(inner, cache);
        return (svc, inner, cache);
    }

    private static PlaceResult Pl(string name) => new(name, name, 0, 0, "photon");

    [Test]
    public async Task Empty_Query_Short_Circuits()
    {
        var (svc, inner, _) = New();
        var results = await svc.SearchAsync("   ");
        await Assert.That(results.Count).IsEqualTo(0);
        await Assert.That(inner.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task First_Call_Hits_Inner_And_Populates_Cache()
    {
        var (svc, inner, cache) = New();
        inner.NextResults = new[] { Pl("Berlin"), Pl("Berlin Wall") };

        var first = await svc.SearchAsync("Berlin");
        await Assert.That(first.Count).IsEqualTo(2);
        await Assert.That(inner.CallCount).IsEqualTo(1);
        // Cache populated.
        await Assert.That(await cache.TryGetAsync("Berlin")).IsNotNull();
    }

    [Test]
    public async Task Repeat_Call_Hits_Cache_Not_Inner()
    {
        var (svc, inner, _) = New();
        inner.NextResults = new[] { Pl("Berlin") };

        await svc.SearchAsync("Berlin");
        await svc.SearchAsync("Berlin");
        await svc.SearchAsync("BERLIN");
        await svc.SearchAsync(" berlin ");

        await Assert.That(inner.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Empty_Inner_Result_Does_Not_Populate_Cache()
    {
        var (svc, inner, cache) = New();
        inner.NextResults = Array.Empty<PlaceResult>();

        await svc.SearchAsync("zzzzz");
        await Assert.That(inner.CallCount).IsEqualTo(1);
        // Empty result not cached -> next call hits inner again.
        await svc.SearchAsync("zzzzz");
        await Assert.That(inner.CallCount).IsEqualTo(2);
        await Assert.That(await cache.TryGetAsync("zzzzz")).IsNull();
    }
}
