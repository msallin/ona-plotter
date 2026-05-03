using OnaPlotter.Services;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Mob;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Tests.Services.Mob;

/// <summary>
/// Pins the MobService contract: local-first raise seeds the store
/// immediately, REST POST runs in the background, the server's WS
/// echo collapses with the local synthetic into one canonical entry,
/// pending raises survive a reload via localStorage, and acknowledge
/// + clear are idempotent against the server.
/// </summary>
public class MobServiceTests
{
    private const string MobPathPrefix = "notifications.mob.";

    /// <summary>Test harness wiring real MobService against fakes.
    /// Every test gets fresh state; the in-memory KV is the
    /// reload-survival surface so persistence assertions can read it
    /// directly.</summary>
    private sealed record Fixture(
        MobService Service,
        FakeNotificationsApi Api,
        ServerNotificationStore Store,
        InMemoryKv Kv,
        TimeProvider Clock,
        List<int> ChangedNotifications);

    private static Fixture NewFixture()
    {
        var api = new FakeNotificationsApi();
        var store = new ServerNotificationStore();
        var kv = new InMemoryKv();
        // Real system clock is fine for the assertions in this
        // suite -- we never advance the clock; the retry loop's
        // backoff is bypassed by setting RaiseDelayMs / FailRaiseCount
        // on the fake API.
        var clock = TimeProvider.System;
        var changes = new List<int>();
        var svc = new MobService(api, store, kv, clock, () => changes.Add(changes.Count));
        return new Fixture(svc, api, store, kv, clock, changes);
    }

    [Test]
    public async Task RaiseAsync_Seeds_Store_Immediately_With_Local_Synthetic()
    {
        // Local-first contract: the alarm pipeline must see the
        // notification before RaiseAsync returns. The REST POST
        // happens in the background.
        var f = NewFixture();
        f.Api.RaiseDelayMs = 1000;   // would NOT have completed by the assertion below

        var localId = await f.Service.RaiseAsync("Person Overboard!", 47.5, 8.5);

        var path = MobPathPrefix + localId;
        var entry = f.Store.Active.FirstOrDefault(n => n.Path == path);
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.State).IsEqualTo("emergency");
        await Assert.That(entry.Latitude).IsEqualTo(47.5);
        await Assert.That(entry.Longitude).IsEqualTo(8.5);
        await Assert.That(entry.Status).IsNotNull();
        // canSilence must be false on emergency state per SK v2 spec.
        await Assert.That(entry.Status!.CanSilence).IsFalse();
    }

    [Test]
    public async Task RaiseAsync_Persists_Pending_Entry_To_Kv()
    {
        var f = NewFixture();
        f.Api.RaiseDelayMs = 1000;

        var localId = await f.Service.RaiseAsync("Person Overboard!", 47.5, 8.5);

        // KV holds the pending queue serialized JSON. Loading via a
        // second MobService instance is the explicit reload path
        // (covered by InitializeAsync_Replays_Persisted_Pending);
        // here we just check the byte was written.
        var stored = await f.Kv.GetAsync("mob.pendingRaise.v1");
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!).Contains(localId);
    }

    [Test]
    public async Task RaiseAsync_Posts_To_Api_With_Message()
    {
        var f = NewFixture();
        await f.Service.RaiseAsync("Crew overboard portside", 47.5, 8.5);

        // Wait for the background POST to land (the fake completes
        // synchronously when RaiseDelayMs is 0).
        await Task.Delay(50);

        await Assert.That(f.Api.RaiseMobCalls.Count).IsEqualTo(1);
        await Assert.That(f.Api.RaiseMobCalls[0]).IsEqualTo("Crew overboard portside");
    }

    [Test]
    public async Task ServerEcho_With_Matching_ServerId_Removes_Local_Synthetic()
    {
        // After REST returns serverId=foo, a WS echo at
        // notifications.mob.foo lands in the store. The local
        // synthetic at notifications.mob.<localId> must go away so
        // the helm sees one banner, not two.
        var f = NewFixture();
        f.Api.NextServerId = "server-foo";

        var localId = await f.Service.RaiseAsync("MOB", 47.5, 8.5);
        // Wait for the background POST to record the serverId on
        // the pending entry.
        await Task.Delay(50);

        // Simulate the WS echo: the SignalkClient parses a delta at
        // notifications.mob.server-foo and calls store.Apply -- we
        // call it directly.
        f.Store.Apply("notifications.mob.server-foo", "emergency", "MOB",
            id: "server-foo",
            status: new NotificationStatus(false, false, false, true, true),
            latitude: 47.5, longitude: 8.5);

        // The local synthetic must be gone; only the server entry
        // remains.
        var paths = f.Store.Active.Select(n => n.Path).ToList();
        await Assert.That(paths).DoesNotContain(MobPathPrefix + localId);
        await Assert.That(paths).Contains("notifications.mob.server-foo");
    }

    [Test]
    public async Task ServerEcho_For_Unknown_ServerId_Treats_As_Remote_Mob()
    {
        // A MOB raised by another plotter on the same server lands
        // here as a fresh path; we don't have a pending raise for
        // it, so it just renders as a remote MOB. Our local
        // synthetic (none in this test) is unaffected.
        var f = NewFixture();
        f.Store.Apply("notifications.mob.someone-else", "emergency", "MOB from buddy",
            id: "someone-else",
            status: new NotificationStatus(false, false, false, true, true),
            latitude: 48.0, longitude: 9.0);

        var entry = f.Store.Active.FirstOrDefault(n => n.Path == "notifications.mob.someone-else");
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.Latitude).IsEqualTo(48.0);
    }

    [Test]
    public async Task RaiseAsync_Survives_Rest_Failure_With_Retry()
    {
        // Per spec: "Never hide or block the alarm because of network
        // failure." After a REST failure the pending entry stays in
        // KV and the synthetic stays in the store; retry is the loop's
        // job.
        var f = NewFixture();
        f.Api.FailRaiseCount = 3;
        f.Api.NextServerId = "after-retries";

        var localId = await f.Service.RaiseAsync("MOB", 47.5, 8.5);

        // Synthetic stays in store while POST is retrying.
        var path = MobPathPrefix + localId;
        await Assert.That(f.Store.Active.Any(n => n.Path == path)).IsTrue();

        // Pending entry stays in KV with attempt count > 0 after
        // failure(s). Wait briefly for the first attempt to fail.
        await Task.Delay(50);
        var stored = await f.Kv.GetAsync("mob.pendingRaise.v1");
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!).Contains(localId);
    }

    [Test]
    public async Task InitializeAsync_Replays_Persisted_Pending_Raises()
    {
        // Reload survival: a pending raise persisted under
        // mob.pendingRaise.v1 must come back in the store + retry
        // loop on the next session.
        var f = NewFixture();
        var saved = "[{\"LocalId\":\"preserved-id\","
                  + "\"Message\":\"MOB\","
                  + "\"Latitude\":47.5,\"Longitude\":8.5,"
                  + "\"CreatedAtUtc\":\"2026-05-03T12:00:00Z\","
                  + "\"AttemptCount\":2,\"ServerId\":null}]";
        await f.Kv.SetAsync("mob.pendingRaise.v1", saved);

        await f.Service.InitializeAsync();

        var path = MobPathPrefix + "preserved-id";
        var entry = f.Store.Active.FirstOrDefault(n => n.Path == path);
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.State).IsEqualTo("emergency");
    }

    [Test]
    public async Task InitializeAsync_Pulls_Active_Server_List_For_Recovery()
    {
        // Restart recovery: a MOB raised pre-reload (or on another
        // plotter) lands via the list endpoint at startup, so the
        // chart paints it within seconds of connect.
        var f = NewFixture();
        f.Api.ListReturn = new Dictionary<string, ServerNotificationDto>
        {
            ["uuid-1"] = new(
                Id: "uuid-1", State: "emergency", Message: "MOB",
                Method: ["visual", "sound"],
                Status: new NotificationStatusDto(false, false, false, true, true),
                Position: new NotificationPositionDto(48.5, 9.5),
                CreatedAt: DateTime.UtcNow),
        };

        await f.Service.InitializeAsync();

        var entry = f.Store.Active.FirstOrDefault(n => n.Path == MobPathPrefix + "uuid-1");
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.Latitude).IsEqualTo(48.5);
    }

    [Test]
    public async Task AcknowledgeAsync_Drops_Local_Entry_And_Posts()
    {
        // Optimistic local update -- the alarm banner clears on the
        // next pipeline tick. Server's WS echo with
        // status.acknowledged=true would land later as a no-op
        // because the entry's already gone.
        var f = NewFixture();
        f.Store.Apply("notifications.mob.foo", "emergency", "MOB",
            id: "foo",
            status: new NotificationStatus(false, false, false, true, true),
            latitude: 47.5, longitude: 8.5);

        var ok = await f.Service.AcknowledgeAsync("foo");

        await Assert.That(ok).IsTrue();
        await Assert.That(f.Store.Active.Any(n => n.Path == "notifications.mob.foo")).IsFalse();
        await Assert.That(f.Api.AckCalls).Contains("foo");
    }

    [Test]
    public async Task ClearAsync_Drops_Local_Entry_And_Posts_Action_Verb()
    {
        // Action-verb clear: POST /{id}/clear (not DELETE). Helm
        // clears via the alarm-banner Clear button.
        var f = NewFixture();
        f.Store.Apply("notifications.mob.bar", "emergency", "MOB",
            id: "bar",
            status: new NotificationStatus(false, false, false, true, true));

        var ok = await f.Service.ClearAsync("bar");

        await Assert.That(ok).IsTrue();
        await Assert.That(f.Store.Active.Any(n => n.Path == "notifications.mob.bar")).IsFalse();
        await Assert.That(f.Api.ClearActionCalls).Contains("bar");
    }

    /// <summary>Shared in-memory KV fake. Same shape the alarm /
    /// settings tests use; declared here for self-containment.</summary>
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

    /// <summary>Recordable INotificationsApi fake. Each verb logs
    /// its argument; raise lets a test pre-load the server-issued
    /// id and a delay so the local-first contract can be observed
    /// during the suspended POST window.</summary>
    private sealed class FakeNotificationsApi : INotificationsApi
    {
        public List<string?> RaiseMobCalls { get; } = [];
        public List<string> AckCalls { get; } = [];
        public List<string> ClearActionCalls { get; } = [];
        public string NextServerId { get; set; } = "server-id-1";
        public int RaiseDelayMs { get; set; } = 0;
        public int FailRaiseCount { get; set; } = 0;
        public IReadOnlyDictionary<string, ServerNotificationDto>? ListReturn { get; set; }

        public async Task<ApiResult<string>> RaiseMobAsync(string? message, CancellationToken ct = default)
        {
            RaiseMobCalls.Add(message);
            if (RaiseDelayMs > 0) await Task.Delay(RaiseDelayMs, ct);
            if (FailRaiseCount > 0)
            {
                FailRaiseCount--;
                return ApiResult<string>.Fail("network");
            }
            return ApiResult<string>.Ok(NextServerId);
        }

        public Task<ApiResult> AcknowledgeAsync(string id, CancellationToken ct = default)
        {
            AckCalls.Add(id);
            return Task.FromResult(ApiResult.Ok);
        }

        public Task<ApiResult> ClearByActionAsync(string id, CancellationToken ct = default)
        {
            ClearActionCalls.Add(id);
            return Task.FromResult(ApiResult.Ok);
        }

        public Task<IReadOnlyDictionary<string, ServerNotificationDto>?> ListActiveAsync(CancellationToken ct = default)
            => Task.FromResult(ListReturn);

        // Path-keyed verbs not exercised by MOB tests (alarm publisher
        // uses them); stubbed for interface compliance.
        public Task<ApiResult> SilenceAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult<string>> RaiseAsync(string path, NotificationPayload body, CancellationToken ct = default)
            => Task.FromResult(ApiResult<string>.Ok("path-id"));
        public Task<ApiResult> ClearAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
    }
}
