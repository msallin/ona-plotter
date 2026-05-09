using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Services;
using OnaPlotter.Services.Auth;

namespace OnaPlotter.Tests;

public class TokenStoreTests
{
    private static (TokenStore store, InMemoryKeyValueStore kv, FakeTimeProvider time) Build()
    {
        var kv = new InMemoryKeyValueStore();
        var time = new FakeTimeProvider(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        return (new TokenStore(kv, time), kv, time);
    }

    [Test]
    public async Task IsValid_False_When_Empty()
    {
        var (store, _, _) = Build();
        await Assert.That(store.IsValid).IsFalse();
        await Assert.That(store.Token).IsNull();
    }

    [Test]
    public async Task SetAsync_Persist_True_Writes_Both_Keys()
    {
        var (store, kv, time) = Build();
        var expiry = time.GetUtcNow().UtcDateTime.AddHours(2);
        await store.SetAsync("jwt-1", expiry, persist: true);

        await Assert.That(kv.Get("auth.token.v1")).IsEqualTo("jwt-1");
        // Round-trips via "O" so the load-side parser sees the same
        // bytes; not asserting the exact format string but that it
        // parses back to the same instant.
        var rawExpiry = kv.Get("auth.expiresAt.v1");
        await Assert.That(rawExpiry).IsNotNull();
        await Assert.That(DateTime.Parse(rawExpiry!,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind)).IsEqualTo(expiry);
    }

    [Test]
    public async Task SetAsync_Persist_False_Does_Not_Write_Storage()
    {
        var (store, kv, time) = Build();
        await store.SetAsync("jwt-2", time.GetUtcNow().UtcDateTime.AddHours(2), persist: false);

        await Assert.That(kv.Get("auth.token.v1")).IsNull();
        await Assert.That(kv.Get("auth.expiresAt.v1")).IsNull();
        // In-memory copy still wins for the duration of the session.
        await Assert.That(store.Token).IsEqualTo("jwt-2");
        await Assert.That(store.IsValid).IsTrue();
    }

    [Test]
    public async Task SetAsync_Persist_False_Wipes_Previous_Persisted_Value()
    {
        // Helm flow: previous session had RememberSession ON → saved
        // a token. New session has RememberSession OFF → SetAsync with
        // persist=false. The earlier persisted bytes must NOT survive
        // (otherwise the next reload resurrects an obsolete token).
        var (store, kv, time) = Build();
        await store.SetAsync("old-jwt", time.GetUtcNow().UtcDateTime.AddHours(2), persist: true);
        await Assert.That(kv.Get("auth.token.v1")).IsEqualTo("old-jwt");

        await store.SetAsync("new-jwt", time.GetUtcNow().UtcDateTime.AddHours(2), persist: false);
        await Assert.That(kv.Get("auth.token.v1")).IsNull();
    }

    [Test]
    public async Task IsValid_False_Within_Skew_Window()
    {
        var (store, _, time) = Build();
        // 10 s remaining < 30 s skew window: treat as already-expired
        // so an in-flight request doesn't 401 on the wire.
        await store.SetAsync("jwt-3", time.GetUtcNow().UtcDateTime.AddSeconds(10), persist: false);
        await Assert.That(store.IsValid).IsFalse();
    }

    [Test]
    public async Task ClearAsync_Drops_Memory_And_Storage()
    {
        var (store, kv, time) = Build();
        await store.SetAsync("jwt-4", time.GetUtcNow().UtcDateTime.AddHours(2), persist: true);

        await store.ClearAsync();

        await Assert.That(store.Token).IsNull();
        await Assert.That(store.IsValid).IsFalse();
        await Assert.That(kv.Get("auth.token.v1")).IsNull();
        await Assert.That(kv.Get("auth.expiresAt.v1")).IsNull();
    }

    [Test]
    public async Task ClearAsync_Idempotent_When_Empty()
    {
        var (store, _, _) = Build();
        int events = 0;
        store.OnTokenChanged += () => events++;
        await store.ClearAsync();
        // Clearing an already-empty store doesn't fire the change
        // event (no transition). Subscribers who rebind the WS on
        // change otherwise reconnect for nothing.
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task LoadAsync_Restores_Persisted_Token()
    {
        var (store1, kv, time) = Build();
        var expiry = time.GetUtcNow().UtcDateTime.AddHours(6);
        await store1.SetAsync("jwt-load", expiry, persist: true);

        // Fresh store, same kv: simulates a tab reload.
        var store2 = new TokenStore(kv, time);
        await store2.LoadAsync();

        await Assert.That(store2.Token).IsEqualTo("jwt-load");
        await Assert.That(store2.ExpiresAtUtc).IsEqualTo(expiry);
        await Assert.That(store2.IsValid).IsTrue();
    }

    [Test]
    public async Task LoadAsync_Drops_Already_Expired_Token()
    {
        // Persist a token, then advance time past its expiry. Reload
        // path must not resurrect it.
        var kv = new InMemoryKeyValueStore();
        var time = new FakeTimeProvider(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var store1 = new TokenStore(kv, time);
        await store1.SetAsync("jwt-expired", time.GetUtcNow().UtcDateTime.AddMinutes(5), persist: true);

        time.Advance(TimeSpan.FromMinutes(10));

        var store2 = new TokenStore(kv, time);
        await store2.LoadAsync();

        await Assert.That(store2.Token).IsNull();
        await Assert.That(store2.IsValid).IsFalse();
        // Expired bytes wiped from storage too.
        await Assert.That(kv.Get("auth.token.v1")).IsNull();
    }

    [Test]
    public async Task LoadAsync_Tolerates_Corrupt_Expiry()
    {
        var kv = new InMemoryKeyValueStore();
        var time = new FakeTimeProvider(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        kv.Set("auth.token.v1", "jwt-x");
        kv.Set("auth.expiresAt.v1", "not-a-date");

        var store = new TokenStore(kv, time);
        await store.LoadAsync();

        await Assert.That(store.Token).IsNull();
        // And the corrupt entry was scrubbed so it can't keep firing.
        await Assert.That(kv.Get("auth.token.v1")).IsNull();
        await Assert.That(kv.Get("auth.expiresAt.v1")).IsNull();
    }

    [Test]
    public async Task OnTokenChanged_Fires_On_New_Value_And_Skips_Same_Value()
    {
        var (store, _, time) = Build();
        int events = 0;
        store.OnTokenChanged += () => events++;
        var expiry = time.GetUtcNow().UtcDateTime.AddHours(2);

        await store.SetAsync("jwt-A", expiry, persist: false);
        await Assert.That(events).IsEqualTo(1);

        // Same JWT + same expiry: no fan-out so subscribers (WS) don't
        // reconnect for nothing.
        await store.SetAsync("jwt-A", expiry, persist: false);
        await Assert.That(events).IsEqualTo(1);

        // Different JWT: fan out.
        await store.SetAsync("jwt-B", expiry, persist: false);
        await Assert.That(events).IsEqualTo(2);
    }

    private sealed class InMemoryKeyValueStore : IKeyValueStore
    {
        private readonly Dictionary<string, string> _data = new();
        public string? Get(string key) => _data.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _data[key] = value;

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Get(key));
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            _data[key] = value;
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            _data.Remove(key);
            return Task.CompletedTask;
        }
    }
}
