using OnaPlotter.Services.Places;

namespace OnaPlotter.Tests.Services.Places;

/// <summary>
/// Pins the fallback contract: primary first, secondary only when
/// primary returns empty. Cancellation between the two calls is
/// honoured so a slow secondary doesn't burn a request slot for an
/// already-stale dropdown.
/// </summary>
public class FallbackPlaceSearchServiceTests
{
    private sealed class StubProvider : IPlaceSearchService
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<PlaceResult> NextResults { get; set; } = [];
        public CancellationToken LastSeenCt { get; private set; }
        public Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct = default)
        {
            CallCount++;
            LastSeenCt = ct;
            return Task.FromResult(NextResults);
        }
    }

    private static PlaceResult Pl(string name, string source) =>
        new(name, name, 0, 0, source);

    [Test]
    public async Task Empty_Query_Hits_Neither_Provider()
    {
        var primary = new StubProvider();
        var secondary = new StubProvider();
        var svc = new FallbackPlaceSearchService(primary, secondary);

        await svc.SearchAsync("   ");

        await Assert.That(primary.CallCount).IsEqualTo(0);
        await Assert.That(secondary.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Primary_Hit_Skips_Secondary()
    {
        var primary = new StubProvider
        {
            NextResults = new[] { Pl("Berlin", "photon") },
        };
        var secondary = new StubProvider
        {
            NextResults = new[] { Pl("Wrong", "nominatim") },
        };
        var svc = new FallbackPlaceSearchService(primary, secondary);

        var r = await svc.SearchAsync("Berlin");

        await Assert.That(r.Count).IsEqualTo(1);
        await Assert.That(r[0].Source).IsEqualTo("photon");
        await Assert.That(primary.CallCount).IsEqualTo(1);
        await Assert.That(secondary.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Primary_Empty_Falls_Through_To_Secondary()
    {
        var primary = new StubProvider();  // empty
        var secondary = new StubProvider
        {
            NextResults = new[] { Pl("Berlin", "nominatim") },
        };
        var svc = new FallbackPlaceSearchService(primary, secondary);

        var r = await svc.SearchAsync("Berlin");

        await Assert.That(r.Count).IsEqualTo(1);
        await Assert.That(r[0].Source).IsEqualTo("nominatim");
        await Assert.That(primary.CallCount).IsEqualTo(1);
        await Assert.That(secondary.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Both_Empty_Returns_Empty()
    {
        var primary = new StubProvider();
        var secondary = new StubProvider();
        var svc = new FallbackPlaceSearchService(primary, secondary);

        var r = await svc.SearchAsync("zzzqxq");

        await Assert.That(r.Count).IsEqualTo(0);
        await Assert.That(primary.CallCount).IsEqualTo(1);
        await Assert.That(secondary.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_Between_Primary_And_Secondary_Skips_Secondary()
    {
        // Helm typed another character while the primary was running;
        // by the time the primary returns the user has moved on. The
        // secondary should NOT fire (no point burning a Nominatim slot
        // for a result that won't render).
        using var cts = new CancellationTokenSource();
        var primary = new StubProvider();  // empty
        var secondary = new StubProvider
        {
            NextResults = new[] { Pl("Late", "nominatim") },
        };
        var svc = new FallbackPlaceSearchService(primary, secondary);

        cts.Cancel();
        var r = await svc.SearchAsync("Berlin", cts.Token);

        await Assert.That(primary.CallCount).IsEqualTo(1);
        await Assert.That(secondary.CallCount).IsEqualTo(0);
        await Assert.That(r.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Cancellation_Token_Threads_Through_To_Both_Providers()
    {
        // Both providers see the same external token so they can
        // honour cancellation themselves (Photon's CallTimeout linked
        // CTS, Nominatim's rate-limit gate).
        using var cts = new CancellationTokenSource();
        var primary = new StubProvider();  // empty
        var secondary = new StubProvider();  // empty
        var svc = new FallbackPlaceSearchService(primary, secondary);

        await svc.SearchAsync("Berlin", cts.Token);

        await Assert.That(primary.LastSeenCt).IsEqualTo(cts.Token);
        await Assert.That(secondary.LastSeenCt).IsEqualTo(cts.Token);
    }
}
