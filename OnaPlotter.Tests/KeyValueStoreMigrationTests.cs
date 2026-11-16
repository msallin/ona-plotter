using OnaPlotter.Services;

namespace OnaPlotter.Tests;

public class KeyValueStoreMigrationTests
{
    [Test]
    public async Task LegacyOnly_MovesValueAndRemovesLegacy()
    {
        var kv = new DictKv();
        await kv.SetAsync("old", "42");

        await KeyValueStoreMigration.MigrateRenamedAsync(kv, [("old", "new.v1")]);

        await Assert.That(await kv.GetAsync("new.v1")).IsEqualTo("42");
        await Assert.That(await kv.GetAsync("old")).IsNull();
    }

    [Test]
    public async Task BothPresent_CurrentWinsAndLegacyIsRemoved()
    {
        // A value written under the new name is newer than any legacy
        // leftover (e.g. an older tab wrote the old key after the upgrade).
        var kv = new DictKv();
        await kv.SetAsync("old", "stale");
        await kv.SetAsync("new.v1", "fresh");

        await KeyValueStoreMigration.MigrateRenamedAsync(kv, [("old", "new.v1")]);

        await Assert.That(await kv.GetAsync("new.v1")).IsEqualTo("fresh");
        await Assert.That(await kv.GetAsync("old")).IsNull();
    }

    [Test]
    public async Task NeitherPresent_WritesNothing()
    {
        var kv = new DictKv();

        await KeyValueStoreMigration.MigrateRenamedAsync(kv, [("old", "new.v1")]);

        await Assert.That(kv.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyStringLegacy_IsMigratedVerbatim()
    {
        // An empty id list ("") is a real stored state (helm disabled
        // every chart) and must not collapse to "missing, use default".
        var kv = new DictKv();
        await kv.SetAsync("old", "");

        await KeyValueStoreMigration.MigrateRenamedAsync(kv, [("old", "new.v1")]);

        await Assert.That(await kv.GetAsync("new.v1")).IsEqualTo("");
    }

    [Test]
    public async Task StorageThrows_LeavesLegacyForNextBoot()
    {
        var kv = new ThrowingOnSetKv();
        await kv.Seed("old", "1");

        await KeyValueStoreMigration.MigrateRenamedAsync(kv, [("old", "new.v1")]);

        await Assert.That(await kv.GetAsync("old")).IsEqualTo("1");
    }

    [Test]
    public async Task SecondRun_IsNoOp()
    {
        var kv = new DictKv();
        await kv.SetAsync("old", "a");
        await KeyValueStoreMigration.MigrateRenamedAsync(kv, [("old", "new.v1")]);
        await kv.SetAsync("new.v1", "b");

        await KeyValueStoreMigration.MigrateRenamedAsync(kv, [("old", "new.v1")]);

        await Assert.That(await kv.GetAsync("new.v1")).IsEqualTo("b");
    }

    [Test]
    public async Task SettingKeys_AllCurrentKeysAreVersioned()
    {
        var current = typeof(SettingKeys)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.Name.StartsWith("Legacy", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        await Assert.That(current.Count).IsGreaterThan(80);
        await Assert.That(current.Where(k => !k.EndsWith(".v1", StringComparison.Ordinal))).IsEmpty();
        await Assert.That(current.Distinct().Count()).IsEqualTo(current.Count);
    }

    [Test]
    public async Task SettingKeys_RenamedPairsMatchLegacySpelling()
    {
        foreach (var (legacy, cur) in SettingKeys.Renamed)
            await Assert.That(cur).IsEqualTo(legacy + ".v1");
    }

    private sealed class DictKv : IKeyValueStore
    {
        private readonly Dictionary<string, string> _d = [];
        public int Count => _d.Count;
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_d.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        { _d[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key, CancellationToken ct = default)
        { _d.Remove(key); return Task.CompletedTask; }
    }

    // Private-browsing Safari: reads work, writes throw QuotaExceededError.
    private sealed class ThrowingOnSetKv : IKeyValueStore
    {
        private readonly Dictionary<string, string> _d = [];
        public Task Seed(string key, string value) { _d[key] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_d.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => throw new Microsoft.JSInterop.JSException("QuotaExceededError");
        public Task RemoveAsync(string key, CancellationToken ct = default)
        { _d.Remove(key); return Task.CompletedTask; }
    }
}
