using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the no-persistence contract for <see cref="IAppSettings.HarborMode"/>.
/// Helm explicitly asked for in-memory only: every fresh page load
/// must start with collision alarms armed so a forgotten Harbor mode
/// can't silently ride into open water. Test mirrors the storage
/// pattern of the other AppSettingsService tests: a fake KV store
/// captures every Set call, and the test asserts no Set landed for
/// the harbor key.
/// </summary>
public class HarborModeSettingsTests
{
    private sealed class FakeKv : IKeyValueStore
    {
        public Dictionary<string, string> Storage { get; } = new();
        // Tracking Get + Set call sites separately so we can pin the
        // "no harbor key was even ATTEMPTED to be loaded" contract --
        // a future regression that adds a LoadBool("harborMode.vN")
        // would surface here even before the Storage dict shows
        // anything.
        public List<string> Gets { get; } = new();
        public List<string> Sets { get; } = new();
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            Gets.Add(key);
            return Task.FromResult(Storage.TryGetValue(key, out var v) ? v : null);
        }
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            Sets.Add(key);
            Storage[key] = value;
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            Storage.Remove(key);
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task HarborMode_DefaultsFalse_OnFreshInitialize()
    {
        var kv = new FakeKv();
        var s = new AppSettingsService(kv);
        await s.InitializeAsync();
        await Assert.That(s.HarborMode).IsFalse();
    }

    [Test]
    public async Task SetHarborModeAsync_FlipsFlag_WithoutPersisting()
    {
        var kv = new FakeKv();
        var s = new AppSettingsService(kv);
        await s.InitializeAsync();

        await s.SetHarborModeAsync(true);
        await Assert.That(s.HarborMode).IsTrue();

        // The crux of the no-persistence contract: a forgotten Harbor
        // mode should never come back on the next reload. Confirmed
        // by inspecting the fake KV: no key matching the harbor flag
        // should have been written.
        bool anyHarborKey = kv.Storage.Keys.Any(k =>
            k.Contains("harbor", StringComparison.OrdinalIgnoreCase));
        await Assert.That(anyHarborKey).IsFalse();
    }

    [Test]
    public async Task SetHarborModeAsync_FiresOnSettingsChanged()
    {
        // The bar's More-menu Harbor button + the AIS push +
        // CpaAlarmRule + the JS setHarborMode call all hang off the
        // OnSettingsChanged event. Without the fire, only the C#
        // flag would change; the rest of the bundle would lag until
        // the next unrelated settings tick.
        var kv = new FakeKv();
        var s = new AppSettingsService(kv);
        await s.InitializeAsync();

        int fired = 0;
        s.OnSettingsChanged += () => fired++;

        await s.SetHarborModeAsync(true);
        await Assert.That(fired).IsEqualTo(1);

        // Toggling to the same value is a no-op (no spurious event).
        await s.SetHarborModeAsync(true);
        await Assert.That(fired).IsEqualTo(1);

        await s.SetHarborModeAsync(false);
        await Assert.That(fired).IsEqualTo(2);
    }

    [Test]
    public async Task HarborMode_DoesNotRehydrate_FromExistingKv()
    {
        // Simulate a previous run where someone (legitimately or via
        // a regression) wrote "true" under any plausible Harbor key.
        // After InitializeAsync the flag must still be false.
        var kv = new FakeKv();
        kv.Storage["harborMode"] = "true";
        kv.Storage["harborMode.v1"] = "true";
        kv.Storage["harbor"] = "true";

        var s = new AppSettingsService(kv);
        await s.InitializeAsync();
        await Assert.That(s.HarborMode).IsFalse();
    }

    [Test]
    public async Task HarborMode_NoLoadAttempt_AtAnyHarborKey()
    {
        // Pin not just "the flag is false" but "no GetAsync was even
        // CALLED for a harbor-prefixed key". A regression that
        // accidentally adds LoadBool("harborMode.v2", false) to
        // InitializeAsync would silently rehydrate from any future
        // KV key the helm typed -- catching that here means CI fails
        // the moment a load path appears, not after a helm complains
        // their forgotten Harbor mode came back on next sail.
        var kv = new FakeKv();
        var s = new AppSettingsService(kv);
        await s.InitializeAsync();

        bool anyHarborKey = kv.Gets.Any(k =>
            k.Contains("harbor", StringComparison.OrdinalIgnoreCase));
        await Assert.That(anyHarborKey).IsFalse();
    }
}
