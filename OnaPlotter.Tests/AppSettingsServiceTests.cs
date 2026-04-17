using OnaPlotter.Services;

namespace OnaPlotter.Tests;

public class AppSettingsServiceTests
{
    [Test]
    public async Task Defaults_WhenStorageEmpty()
    {
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();

        await Assert.That(svc.NightMode).IsFalse();
        await Assert.That(svc.MapOrientation).IsEqualTo("north");
        await Assert.That(svc.FollowBoat).IsTrue();
        await Assert.That(svc.LaylinesVisible).IsFalse();
        await Assert.That(svc.DepthAlarmThreshold).IsEqualTo(3.0);
        await Assert.That(svc.CpaAlarmThreshold).IsEqualTo(0.5);
        await Assert.That(svc.WindShiftAlarmThreshold).IsEqualTo(15.0);
    }

    [Test]
    public async Task LoadsPersistedValues()
    {
        var kv = new InMemoryKv();
        await kv.SetAsync("nightMode", "true");
        await kv.SetAsync("mapOrientation", "course");
        await kv.SetAsync("depthAlarmThreshold", "5.5");

        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await Assert.That(svc.NightMode).IsTrue();
        await Assert.That(svc.MapOrientation).IsEqualTo("course");
        await Assert.That(svc.DepthAlarmThreshold).IsEqualTo(5.5);
    }

    [Test]
    public async Task SetPersists()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetNightModeAsync(true);
        await svc.SetDepthAlarmThresholdAsync(4.2);

        await Assert.That(await kv.GetAsync("nightMode")).IsEqualTo("true");
        await Assert.That(await kv.GetAsync("depthAlarmThreshold")).IsEqualTo("4.2");
    }

    [Test]
    public async Task InvariantCultureOnDoubles()
    {
        // Ensures persisted doubles use '.' regardless of OS locale.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetCpaAlarmThresholdAsync(0.75);

        var stored = await kv.GetAsync("cpaAlarmThreshold");
        await Assert.That(stored).IsEqualTo("0.75");
    }

    [Test]
    public async Task OnSettingsChanged_Fires()
    {
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        int calls = 0;
        svc.OnSettingsChanged += () => calls++;

        await svc.SetNightModeAsync(true);
        await svc.SetDepthAlarmThresholdAsync(6);

        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task EnabledCharts_RoundTrip()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetEnabledChartsAsync(["OSM", "OpenSeaMap", "navionics_x"]);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();

        await Assert.That(svc2.EnabledChartIds.Count).IsEqualTo(3);
        await Assert.That(svc2.EnabledChartIds.Contains("OpenSeaMap")).IsTrue();
        await Assert.That(svc2.EnabledChartIds.Contains("navionics_x")).IsTrue();
    }

    [Test]
    public async Task EnabledCharts_Empty_LoadsEmpty()
    {
        // Empty stored value should not produce a single empty-string element.
        var kv = new InMemoryKv();
        await kv.SetAsync("enabledChartIds", "");

        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await Assert.That(svc.EnabledChartIds.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EnabledRoutes_Overwrite()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetEnabledRoutesAsync(["a", "b"]);
        await svc.SetEnabledRoutesAsync(["c"]);

        await Assert.That(svc.EnabledRouteIds.Count).IsEqualTo(1);
        await Assert.That(svc.EnabledRouteIds.Contains("c")).IsTrue();
    }

    [Test]
    public async Task ConcurrentInitialize_RunsOnce()
    {
        // Race condition test: many pages call InitializeAsync simultaneously.
        var kv = new CountingKv();
        var svc = new AppSettingsService(kv);

        var tasks = Enumerable.Range(0, 20).Select(_ => svc.InitializeAsync()).ToArray();
        await Task.WhenAll(tasks);

        // Each key should have been read exactly once, not 20 times.
        await Assert.That(kv.GetCount("nightMode")).IsEqualTo(1);
    }

    private sealed class InMemoryKv : IKeyValueStore
    {
        private readonly Dictionary<string, string> _d = [];
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_d.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        { _d[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key, CancellationToken ct = default)
        { _d.Remove(key); return Task.CompletedTask; }
    }

    private sealed class CountingKv : IKeyValueStore
    {
        private readonly Dictionary<string, int> _counts = [];
        public int GetCount(string key) => _counts.GetValueOrDefault(key, 0);
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            _counts[key] = _counts.GetValueOrDefault(key, 0) + 1;
            return Task.FromResult<string?>(null);
        }
        public Task SetAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
