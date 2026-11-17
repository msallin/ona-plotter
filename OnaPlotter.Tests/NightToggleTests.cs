using OnaPlotter.Services;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class NightToggleTests
{
    [Test]
    [Arguments("day", "day")]
    [Arguments("dawn", "day")]
    [Arguments("dusk", "night")]
    [Arguments("night", "night")]
    [Arguments("", null)]
    [Arguments(null, null)]
    [Arguments("nauticalDawn", null)]
    public async Task SunCluster_MapsToTwoStates(string? state, string? expected)
    {
        await Assert.That(NightToggle.SunCluster(state)).IsEqualTo(expected);
    }

    [Test]
    public async Task Toggle_FlipsNightMode_AndRecordsTheManualOverrideCluster()
    {
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        int changes = 0;
        svc.OnSettingsChanged += () => changes++;

        await NightToggle.ToggleAsync(svc, "dusk");

        await Assert.That(svc.NightMode).IsTrue();
        await Assert.That(svc.LastManualNightOverrideSunCluster).IsEqualTo("night");
        await Assert.That(changes).IsGreaterThan(0);

        await NightToggle.ToggleAsync(svc, "dusk");
        await Assert.That(svc.NightMode).IsFalse();
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
}
