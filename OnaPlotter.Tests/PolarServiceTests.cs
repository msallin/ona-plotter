using OnaPlotter.Services;

namespace OnaPlotter.Tests;

public class PolarServiceTests
{
    private const string SampleCsv =
        "TWA,6,8,10,12\n" +
        "45,4.0,5.0,5.5,5.8\n" +
        "60,4.5,5.5,6.0,6.3\n" +
        "90,5.0,6.0,6.5,6.8\n" +
        "135,4.8,5.8,6.2,6.5";

    private static PolarService NewService() => new(new InMemoryKeyValueStore());

    [Test]
    public async Task HasPolar_FalseWhenEmpty()
    {
        var svc = NewService();
        await Assert.That(svc.HasPolar).IsFalse();
    }

    [Test]
    public async Task ImportAsync_ParsesHeaders()
    {
        var svc = NewService();
        await svc.ImportAsync(SampleCsv);

        await Assert.That(svc.HasPolar).IsTrue();
        await Assert.That(svc.TwsValues).IsEquivalentTo(new[] { 6.0, 8.0, 10.0, 12.0 });
        await Assert.That(svc.TwaValues).IsEquivalentTo(new[] { 45.0, 60.0, 90.0, 135.0 });
    }

    [Test]
    public async Task GetTargetSpeed_ExactMatch()
    {
        var svc = NewService();
        await svc.ImportAsync(SampleCsv);

        // TWA=60, TWS=10 -> row 2 col 3 = 6.0
        var v = svc.GetTargetSpeed(60, 10);
        await Assert.That(v).IsEqualTo(6.0);
    }

    [Test]
    public async Task GetTargetSpeed_Interpolates()
    {
        var svc = NewService();
        await svc.ImportAsync(SampleCsv);

        // TWA=52.5 (halfway 45-60), TWS=10 (exact)
        // 45,10 = 5.5; 60,10 = 6.0; midpoint = 5.75
        var v = svc.GetTargetSpeed(52.5, 10);
        await Assert.That(v).IsEqualTo(5.75);
    }

    [Test]
    public async Task GetTargetSpeed_NegativeTwaTreatedAsAbs()
    {
        var svc = NewService();
        await svc.ImportAsync(SampleCsv);

        // -60 should give the same as +60.
        var pos = svc.GetTargetSpeed(60, 10);
        var neg = svc.GetTargetSpeed(-60, 10);
        await Assert.That(neg).IsEqualTo(pos);
    }

    [Test]
    public async Task GetTargetSpeed_ClampsBeyondEdges()
    {
        var svc = NewService();
        await svc.ImportAsync(SampleCsv);

        // TWS below smallest (6) clamps to that column.
        var low = svc.GetTargetSpeed(45, 2);
        await Assert.That(low).IsEqualTo(4.0);

        // TWS above largest (12) clamps to that column.
        var high = svc.GetTargetSpeed(45, 30);
        await Assert.That(high).IsEqualTo(5.8);
    }

    [Test]
    public async Task GetPerformance_ReturnsRatio()
    {
        var svc = NewService();
        await svc.ImportAsync(SampleCsv);

        // Target at 60,10 is 6.0. Actual 3.0 -> 0.5
        var perf = svc.GetPerformance(3.0, 60, 10);
        await Assert.That(perf).IsEqualTo(0.5);
    }

    [Test]
    public async Task GetPerformance_NullWithoutPolar()
    {
        var svc = NewService();
        await Assert.That(svc.GetPerformance(5, 45, 10)).IsNull();
    }

    [Test]
    public async Task GetPolarCurve_ReturnsAllTwaPoints()
    {
        var svc = NewService();
        await svc.ImportAsync(SampleCsv);

        var curve = svc.GetPolarCurve(10);
        await Assert.That(curve.Length).IsEqualTo(4);
        await Assert.That(curve[0].Twa).IsEqualTo(45);
        await Assert.That(curve[0].Speed).IsEqualTo(5.5);
    }

    [Test]
    public async Task ClearAsync_ResetsState()
    {
        var svc = NewService();
        await svc.ImportAsync(SampleCsv);
        await svc.ClearAsync();

        await Assert.That(svc.HasPolar).IsFalse();
    }

    [Test]
    public async Task Parse_ThrowsOnEmptyCsv()
    {
        var svc = NewService();
        await Assert.That(() => svc.ImportAsync("")).ThrowsExactly<FormatException>();
    }

    [Test]
    public async Task Parse_ThrowsOnMalformedHeader()
    {
        var svc = NewService();
        await Assert.That(() => svc.ImportAsync("TWA,oops\n45,4.0")).ThrowsExactly<FormatException>();
    }

    [Test]
    public async Task InitializeAsync_LoadsFromStorage()
    {
        var store = new InMemoryKeyValueStore();
        await store.SetAsync("polar", SampleCsv);

        var svc = new PolarService(store);
        await svc.InitializeAsync();

        await Assert.That(svc.HasPolar).IsTrue();
    }

    private sealed class InMemoryKeyValueStore : IKeyValueStore
    {
        private readonly Dictionary<string, string> _store = [];
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        { _store[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key, CancellationToken ct = default)
        { _store.Remove(key); return Task.CompletedTask; }
    }

    // --- Optimal VMG --------------------------------------------

    [Test]
    public async Task GetOptimalUpwind_NoPolar_ReturnsNull()
    {
        await Assert.That(NewService().GetOptimalUpwind(10)).IsNull();
    }

    [Test]
    public async Task GetOptimalUpwind_LandsInUpwindRange()
    {
        var svc = NewService();
        await svc.ImportAsync(SampleCsv);

        var opt = svc.GetOptimalUpwind(10);
        await Assert.That(opt).IsNotNull();
        // Must be in the upwind quadrant (we search 20..90 deg).
        await Assert.That(opt!.Value.TwaDeg >= 20).IsTrue();
        await Assert.That(opt.Value.TwaDeg <= 90).IsTrue();
        // VMG is positive along the wind axis upwind.
        await Assert.That(opt.Value.VmgKn > 0).IsTrue();
    }

    [Test]
    public async Task GetOptimalDownwind_LandsInDownwindRange()
    {
        var svc = NewService();
        await svc.ImportAsync(SampleCsv);

        var opt = svc.GetOptimalDownwind(10);
        await Assert.That(opt).IsNotNull();
        await Assert.That(opt!.Value.TwaDeg >= 90).IsTrue();
        await Assert.That(opt.Value.TwaDeg <= 170).IsTrue();
        await Assert.That(opt.Value.VmgKn > 0).IsTrue();
    }

    [Test]
    public async Task GetOptimalUpwind_MaximisesVmgRelativeToNeighbours()
    {
        var svc = NewService();
        await svc.ImportAsync(SampleCsv);

        var opt = svc.GetOptimalUpwind(10);
        await Assert.That(opt).IsNotNull();

        // Anywhere else in the upwind quadrant produces lower VMG.
        var optTwa = opt!.Value.TwaDeg;
        foreach (int twa in new[] { 25, 40, 55, 70, 85 })
        {
            if (Math.Abs(twa - optTwa) < 3) continue;       // near the peak
            double? bsp = svc.GetTargetSpeed(twa, 10);
            if (bsp is null) continue;
            double vmg = bsp.Value * Math.Abs(Math.Cos(twa * Math.PI / 180.0));
            await Assert.That(vmg <= opt.Value.VmgKn + 1e-6).IsTrue();
        }
    }

    [Test]
    public async Task GetOptimal_ZeroWind_DoesNotThrow()
    {
        // Zero TWS is below the sample polar's lowest column (6 kn).
        // GetTargetSpeed clamps at the boundary -- documented behaviour,
        // treats "below sample range" as "use the lowest row". A strict
        // out-of-range null would be more correct for extrapolation but
        // every existing caller (Dashboard, router) handles the clamped
        // value fine. What we DO NOT accept is a throw, a NaN, or an
        // infinity -- those would crash the router's frontier expansion
        // silently.
        var svc = NewService();
        await svc.ImportAsync(SampleCsv);

        var opt = svc.GetOptimalUpwind(0);
        if (opt is not null)
        {
            await Assert.That(double.IsNaN(opt.Value.VmgKn)).IsFalse();
            await Assert.That(double.IsInfinity(opt.Value.VmgKn)).IsFalse();
            await Assert.That(opt.Value.VmgKn >= 0).IsTrue();
        }
    }
}
