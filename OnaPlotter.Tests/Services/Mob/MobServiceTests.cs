using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Mob;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Tests.Services.Mob;

/// <summary>
/// Pins the MobService contract from both sides: the local-first
/// happy path (raise -> POST -> WS echo -> single canonical entry)
/// and the failure / recovery edges (offline retry, late sync,
/// duplicates, reload, dispose).
///
/// <para>Every test runs against a hand-rolled fake API + store +
/// in-memory KV + a <see cref="FakeTimeProvider"/> so the retry-
/// loop backoff is deterministic. The retry-loop Task is exposed
/// by <c>MobService.GetRaiseLoopForTest</c> and awaited explicitly
/// instead of polling on a wall-clock <c>Task.Delay</c>; same for
/// observing pending-state changes - the tests don't sleep.</para>
/// </summary>
public class MobServiceTests
{
    private const string MobPathPrefix = "notifications.mob.";

    /// <summary>Test harness wiring real MobService against fakes.
    /// Implements IDisposable so every test uses
    /// <c>using var f = NewFixture()</c> - guarantees the retry
    /// loop is cancelled at scope exit even if an assertion fails.
    /// Without this, a Task.Run loop suspended on FakeTimeProvider
    /// Delay or on the fake API's SuspendRaise gate would leak into
    /// the next test and the dotnet runner would hang at suite
    /// shutdown waiting for the parked tasks.</summary>
    private sealed class Fixture : IDisposable
    {
        public MobService Service { get; }
        public FakeNotificationsApi Api { get; }
        public ServerNotificationStore Store { get; }
        public InMemoryKv Kv { get; }
        public FakeTimeProvider Clock { get; }

        public Fixture(MobService svc, FakeNotificationsApi api,
            ServerNotificationStore store, InMemoryKv kv, FakeTimeProvider clock)
        {
            Service = svc;
            Api = api;
            Store = store;
            Kv = kv;
            Clock = clock;
        }

        public void Dispose() => Service.Dispose();
    }

    private static Fixture NewFixture()
    {
        var api = new FakeNotificationsApi();
        var store = new ServerNotificationStore();
        var kv = new InMemoryKv();
        var positions = new ResolvedPositionStore(kv);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero));
        var svc = new MobService(api, store, kv, positions, clock);
        return new Fixture(svc, api, store, kv, clock);
    }

    /// <summary>Awaits the background retry loop for a localId. Used
    /// in place of wall-clock Task.Delay so tests are deterministic.
    /// Returns immediately if the loop already exited (success or
    /// cancel); otherwise blocks until the Task.Run handle completes.
    /// Tests that expect the loop to PARK indefinitely (e.g. on a
    /// FakeTimeProvider Task.Delay with no advance) must spin-wait
    /// on observable state instead - see <see cref="WaitForRaiseCountAsync"/>.</summary>
    private static Task AwaitRaiseLoopAsync(MobService svc, string localId)
    {
        var t = svc.GetRaiseLoopForTest(localId);
        return t ?? Task.CompletedTask;
    }

    /// <summary>Spin-wait until <paramref name="api"/> has recorded
    /// at least <paramref name="target"/> RaiseMobAsync calls, or the
    /// timeout expires. Use in tests that rely on FakeTimeProvider
    /// Task.Delay parks: the test must observe each iteration's POST
    /// land before advancing the clock for the next backoff window,
    /// because Clock.Advance is a no-op for timers that haven't been
    /// registered yet.</summary>
    private static async Task WaitForRaiseCountAsync(
        FakeNotificationsApi api, int target,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (api.RaiseMobCalls.Count < target && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    // ------------------------------------------------------------
    // Local-first happy path
    // ------------------------------------------------------------

    [Test]
    public async Task RaiseAsync_Seeds_Store_Immediately_With_Local_Synthetic()
    {
        // Local-first contract: the alarm pipeline must see the
        // notification before the background POST returns.
        using var f = NewFixture();
        f.Api.SuspendRaise = true;   // POST never completes within the test

        var localId = await f.Service.RaiseAsync("Person Overboard!", 47.5, 8.5);

        var path = MobPathPrefix + localId;
        var entry = f.Store.Active.FirstOrDefault(n => n.Path == path);
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.State).IsEqualTo("emergency");
        await Assert.That(entry.Latitude).IsEqualTo(47.5);
        await Assert.That(entry.Longitude).IsEqualTo(8.5);
        // canSilence must be false on emergency state per SK v2 spec.
        await Assert.That(entry.Status).IsNotNull();
        await Assert.That(entry.Status!.CanSilence).IsFalse();
    }

    [Test]
    public async Task RaiseAsync_With_Null_Message_Uses_Default_Banner_Copy()
    {
        // TEST-010: the default-message branch was previously untested.
        using var f = NewFixture();
        f.Api.SuspendRaise = true;

        var localId = await f.Service.RaiseAsync(null, 47.5, 8.5);

        var entry = f.Store.Active.First(n => n.Path == MobPathPrefix + localId);
        await Assert.That(entry.Message).IsEqualTo(MobService.DefaultRaiseMessage);
    }

    [Test]
    public async Task RaiseAsync_With_Whitespace_Message_Falls_Back_To_Default()
    {
        // TEST-010: covers the IsNullOrWhiteSpace half of the
        // default-message branch.
        using var f = NewFixture();
        f.Api.SuspendRaise = true;

        var localId = await f.Service.RaiseAsync("   ", 47.5, 8.5);

        var entry = f.Store.Active.First(n => n.Path == MobPathPrefix + localId);
        await Assert.That(entry.Message).IsEqualTo(MobService.DefaultRaiseMessage);
    }

    [Test]
    public async Task RaiseAsync_With_Null_Position_Stores_Entry_Without_Coords()
    {
        // TEST-014: the SK MOB notification value's position block
        // is allowed to be null (no GPS at trigger time). Pin that
        // the service handles it without throwing.
        using var f = NewFixture();
        f.Api.SuspendRaise = true;

        var localId = await f.Service.RaiseAsync("MOB", latitude: null, longitude: null);

        var entry = f.Store.Active.First(n => n.Path == MobPathPrefix + localId);
        await Assert.That(entry.Latitude).IsNull();
        await Assert.That(entry.Longitude).IsNull();
    }

    [Test]
    public async Task RaiseAsync_Persists_Pending_Entry_To_Kv()
    {
        using var f = NewFixture();
        f.Api.SuspendRaise = true;

        var localId = await f.Service.RaiseAsync("MOB", 47.5, 8.5);

        var stored = await f.Kv.GetAsync("mob.pendingRaise.v1");
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!).Contains(localId);
    }

    [Test]
    public async Task RaiseAsync_Posts_To_Api_With_Message()
    {
        using var f = NewFixture();

        var localId = await f.Service.RaiseAsync("Crew overboard portside", 47.5, 8.5);
        await AwaitRaiseLoopAsync(f.Service, localId);

        await Assert.That(f.Api.RaiseMobCalls.Count).IsEqualTo(1);
        await Assert.That(f.Api.RaiseMobCalls[0]).IsEqualTo("Crew overboard portside");
    }

    [Test]
    public async Task ServerEcho_With_Matching_ServerId_Removes_Local_Synthetic()
    {
        // Acceptance scenario 1 (happy path): REST returns serverId,
        // WS echo lands at the server-twin path, the local synthetic
        // is collapsed away so the helm sees one banner.
        using var f = NewFixture();
        f.Api.NextServerId = "server-foo";

        var localId = await f.Service.RaiseAsync("MOB", 47.5, 8.5);
        await AwaitRaiseLoopAsync(f.Service, localId);

        // Simulate the WS echo arriving via the standard WS path.
        f.Store.Apply("notifications.mob.server-foo", "emergency", "MOB",
            id: "server-foo",
            status: new NotificationStatus(false, false, false, true, true),
            latitude: 47.5, longitude: 8.5);

        var paths = f.Store.Active.Select(n => n.Path).ToList();
        await Assert.That(paths).DoesNotContain(MobPathPrefix + localId);
        await Assert.That(paths).Contains("notifications.mob.server-foo");
    }

    [Test]
    public async Task ServerEcho_Without_Position_Inherits_Position_From_Pending()
    {
        // signalk-server discards the position field from the /mob
        // POST body so the WS echo arrives at notifications.mob.<id>
        // with position=null. The local synthetic carried the helm's
        // recorded fix; the reconcile flow must transfer that fix
        // onto the server-twin store entry before clearing the
        // synthetic, otherwise the chart marker has no coords to
        // render.
        using var f = NewFixture();
        f.Api.NextServerId = "srv-no-pos";

        var localId = await f.Service.RaiseAsync("MOB", 47.5, 8.5);
        await AwaitRaiseLoopAsync(f.Service, localId);

        // WS echo arrives at the server-twin path WITHOUT position
        // (matches signalk-server's actual behaviour).
        f.Store.Apply("notifications.mob.srv-no-pos", "emergency", "MOB",
            id: "srv-no-pos",
            status: new NotificationStatus(false, false, false, true, true),
            latitude: null, longitude: null);

        var serverEntry = f.Store.Active.FirstOrDefault(
            n => n.Path == "notifications.mob.srv-no-pos");
        await Assert.That(serverEntry).IsNotNull();
        await Assert.That(serverEntry!.Latitude).IsEqualTo(47.5);
        await Assert.That(serverEntry.Longitude).IsEqualTo(8.5);
        // Local synthetic was torn down - single banner / single marker.
        await Assert.That(f.Store.Active.Any(n => n.Path == MobPathPrefix + localId)).IsFalse();
        // Resolved-position cache was persisted under the serverId
        // so a future reload / restart can recover the coords.
        var cached = await f.Kv.GetAsync("mob.resolvedPositions.v1");
        await Assert.That(cached).IsNotNull();
        await Assert.That(cached!).Contains("srv-no-pos");
        await Assert.That(cached).Contains("47.5");
    }

    [Test]
    public async Task ServerEcho_Arriving_Before_ServerId_Recorded_Still_Removes_Local_Synthetic()
    {
        // Race regression: in the field the helm saw TWO banners on
        // the local plotter (local synthetic + server twin).
        // Cause: the SK server's WS push of notifications.mob.<serverId>
        // can arrive at the SignalkClient BEFORE the REST POST reply
        // returns to MobService. ReconcileMobPath then ran with
        // pending.ServerId == null, missed the match, and the local
        // synthetic stayed armed alongside the server twin.
        // Fix: after MobService records ServerId, it probes the store
        // for the server-twin path and tears the synthetic down if
        // the WS echo already landed.
        using var f = NewFixture();
        f.Api.SuspendRaise = true;
        f.Api.NextServerId = "race-srv";

        var localId = await f.Service.RaiseAsync("MOB", 47.5, 8.5);
        var localPath = MobPathPrefix + localId;
        await Assert.That(f.Store.Active.Any(n => n.Path == localPath)).IsTrue();
        // Wait for the loop to be parked at the gate.
        await f.Api.WaitForSuspendedRaiseAsync();

        // Server-twin WS echo arrives WHILE the REST POST is still
        // suspended - ServerId hasn't been written into pending yet.
        // OnPathChanged fires; ReconcileMobPath has nothing to match.
        f.Store.Apply("notifications.mob.race-srv", "emergency", "MOB",
            id: "race-srv",
            status: new NotificationStatus(false, false, false, true, true),
            latitude: 47.5, longitude: 8.5);
        // Both entries are in the store right now - this is the
        // duplicate-banner state.
        await Assert.That(f.Store.Active.Any(n => n.Path == localPath)).IsTrue();
        await Assert.That(f.Store.Active.Any(n => n.Path == "notifications.mob.race-srv")).IsTrue();

        // Release the gate so the REST POST returns. MobService
        // records ServerId, then probes the store and tears the
        // local synthetic down.
        f.Api.SuspendRaise = false;
        await AwaitRaiseLoopAsync(f.Service, localId);

        var paths = f.Store.Active.Select(n => n.Path).ToList();
        await Assert.That(paths).DoesNotContain(localPath);
        await Assert.That(paths).Contains("notifications.mob.race-srv");
    }

    [Test]
    public async Task ServerEcho_For_Unknown_ServerId_Treats_As_Remote_Mob()
    {
        // Acceptance scenario 4: another plotter raises a MOB; this
        // plotter receives the WS delta and shows it.
        using var f = NewFixture();

        f.Store.Apply("notifications.mob.someone-else", "emergency", "MOB from buddy",
            id: "someone-else",
            status: new NotificationStatus(false, false, false, true, true),
            latitude: 48.0, longitude: 9.0);

        var entry = f.Store.Active.FirstOrDefault(n => n.Path == "notifications.mob.someone-else");
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.Latitude).IsEqualTo(48.0);
    }

    // ------------------------------------------------------------
    // Multi-MOB + late-sync (acceptance scenarios 6 + 3)
    // ------------------------------------------------------------

    [Test]
    public async Task RaiseAsync_Twice_Produces_Two_Distinct_Entries()
    {
        // Acceptance scenario 6: two casualties render as two
        // entries, never collapsed into one.
        using var f = NewFixture();
        f.Api.SuspendRaise = true;   // keep both locally pending so the assertion
                                     // observes the dual state directly.

        var id1 = await f.Service.RaiseAsync("MOB starboard", 47.5, 8.5);
        var id2 = await f.Service.RaiseAsync("MOB port", 48.0, 9.0);

        await Assert.That(id1).IsNotEqualTo(id2);
        var mobPaths = f.Store.Active
            .Where(n => n.Path.StartsWith(MobPathPrefix, StringComparison.Ordinal))
            .Select(n => n.Path)
            .ToList();
        await Assert.That(mobPaths.Count).IsEqualTo(2);
        await Assert.That(mobPaths).Contains(MobPathPrefix + id1);
        await Assert.That(mobPaths).Contains(MobPathPrefix + id2);
    }

    [Test]
    public async Task Late_Sync_After_Retry_Collapses_To_Single_Entry()
    {
        // Acceptance scenario 3: first 2 POSTs fail, third succeeds
        // (offline window healed); the WS echo of the server twin
        // then drops the synthetic so the helm still sees one entry.
        using var f = NewFixture();
        f.Api.FailRaiseCount = 2;
        f.Api.NextServerId = "after-retries";

        var localId = await f.Service.RaiseAsync("MOB", 47.5, 8.5);
        // First attempt is unconditional (no backoff). Wait for it
        // to land before advancing the clock - a Clock.Advance
        // before the loop has registered its Task.Delay timer is a
        // no-op, leaving the timer parked at the wrong deadline.
        await WaitForRaiseCountAsync(f.Api, 1);
        // Step past the 1s backoff for attempt 2, wait for it.
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForRaiseCountAsync(f.Api, 2);
        // Step past the 2s backoff for attempt 3 (success), then
        // await the loop to fully unwind through the success path.
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        await AwaitRaiseLoopAsync(f.Service, localId);

        await Assert.That(f.Api.RaiseMobCalls.Count).IsEqualTo(3);

        // WS echo arrives at the server-twin path.
        f.Store.Apply("notifications.mob.after-retries", "emergency", "MOB",
            id: "after-retries",
            status: new NotificationStatus(false, false, false, true, true),
            latitude: 47.5, longitude: 8.5);

        var mobPaths = f.Store.Active
            .Where(n => n.Path.StartsWith(MobPathPrefix, StringComparison.Ordinal))
            .Select(n => n.Path)
            .ToList();
        await Assert.That(mobPaths).DoesNotContain(MobPathPrefix + localId);
        await Assert.That(mobPaths.Count).IsEqualTo(1);
        await Assert.That(mobPaths).Contains("notifications.mob.after-retries");
    }

    [Test]
    public async Task RaiseAsync_Survives_Rest_Failure_Synthetic_Stays()
    {
        // Per spec: "Never hide or block the alarm because of network
        // failure." After a REST failure the pending entry stays in
        // KV and the synthetic stays in the store; retry is the loop's
        // job.
        using var f = NewFixture();
        f.Api.FailRaiseCount = 100;   // never succeed; keep pending pinned

        var localId = await f.Service.RaiseAsync("MOB", 47.5, 8.5);
        // Let the first attempt complete (no backoff needed yet).
        // The loop pauses on the next backoff Delay; we don't advance
        // the clock so it stays parked and the assertions observe a
        // stable snapshot.

        var path = MobPathPrefix + localId;
        await Assert.That(f.Store.Active.Any(n => n.Path == path)).IsTrue();
        var stored = await f.Kv.GetAsync("mob.pendingRaise.v1");
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!).Contains(localId);
    }

    // ------------------------------------------------------------
    // Reload + restart recovery (acceptance scenario 7)
    // ------------------------------------------------------------

    [Test]
    public async Task InitializeAsync_Replays_Persisted_Pending_Raises()
    {
        // Reload survival: a pending raise persisted under
        // mob.pendingRaise.v1 must come back in the store + retry
        // loop on the next session.
        using var f = NewFixture();
        var saved = "[{\"LocalId\":\"preserved-id\","
                  + "\"Message\":\"MOB\","
                  + "\"Latitude\":47.5,\"Longitude\":8.5,"
                  + "\"AttemptCount\":2,\"ServerId\":null}]";
        await f.Kv.SetAsync("mob.pendingRaise.v1", saved);
        f.Api.SuspendRaise = true;

        await f.Service.InitializeAsync();

        var path = MobPathPrefix + "preserved-id";
        var entry = f.Store.Active.FirstOrDefault(n => n.Path == path);
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.State).IsEqualTo("emergency");
    }

    [Test]
    public async Task InitializeAsync_Replays_With_Saved_AttemptCount_Backoff()
    {
        // TEST-009: persisted AttemptCount=2 means "iteration 2 just
        // failed"; the next iteration's wait must therefore match
        // what iteration 3 would wait (idx = attempt - 1 = 1 -> 2s),
        // not the initial 1s. Pin the contract: a reload mid-retry
        // must not flood the server with 1s-spaced retries.
        using var f = NewFixture();
        var saved = "[{\"LocalId\":\"slow-id\","
                  + "\"Message\":\"MOB\","
                  + "\"Latitude\":47.5,\"Longitude\":8.5,"
                  + "\"AttemptCount\":2,\"ServerId\":null}]";
        await f.Kv.SetAsync("mob.pendingRaise.v1", saved);
        f.Api.NextServerId = "after-resume";

        await f.Service.InitializeAsync();
        // Yield so the replayed Task.Run can register its first
        // Task.Delay timer with the FakeTimeProvider before we
        // advance - otherwise the advance fires no timers and the
        // delay parks at the wrong deadline.
        await Task.Delay(50);

        // After 1s, the saved AttemptCount=2 wait of 2s hasn't
        // expired - no POST yet.
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(20);
        await Assert.That(f.Api.RaiseMobCalls.Count).IsEqualTo(0);

        // Step past the 2s backoff -> attempt fires.
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitForRaiseCountAsync(f.Api, 1);
        await Assert.That(f.Api.RaiseMobCalls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task InitializeAsync_With_Corrupt_Persisted_Json_Starts_Empty()
    {
        // TEST-013: a corrupt localStorage value falls back to "no
        // pending" instead of crashing on the first persist.
        using var f = NewFixture();
        await f.Kv.SetAsync("mob.pendingRaise.v1", "{not valid json");

        await f.Service.InitializeAsync();

        await Assert.That(f.Store.Active.Any(n => n.Path.StartsWith(MobPathPrefix))).IsFalse();
    }

    [Test]
    public async Task InitializeAsync_Pulls_Active_Server_List_For_Recovery()
    {
        // Acceptance scenario 7: a MOB raised pre-reload (or on
        // another plotter) lands via the list endpoint at startup.
        using var f = NewFixture();
        f.Api.ListReturn = new Dictionary<string, ServerNotificationEnvelope>
        {
            ["uuid-1"] = new(
                Context: "vessels.self",
                Path: "notifications.mob.uuid-1",
                Value: new ServerNotificationDto(
                    Id: "uuid-1", State: "emergency", Message: "MOB",
                    Method: ["visual", "sound"],
                    Status: new NotificationStatusDto(false, false, false, true, true),
                    Position: new NotificationPositionDto(48.5, 9.5),
                    CreatedAt: DateTime.UtcNow)),
        };

        await f.Service.InitializeAsync();

        var entry = f.Store.Active.FirstOrDefault(n => n.Path == MobPathPrefix + "uuid-1");
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.Latitude).IsEqualTo(48.5);
    }

    [Test]
    public async Task InitializeAsync_Recovers_Position_From_Resolved_Cache()
    {
        // Helm regression: signalk-server discards the position
        // field from the /mob POST body, so the WS echo and the
        // /notifications GET both arrive with position=null. On
        // a clean restart (no persisted PendingRaise to fall back
        // on), the chart marker disappeared because MobChartRenderer
        // bails on null lat/lon. The resolved-position cache,
        // written by reconcile in the previous session, is the
        // recovery path: when ListActiveAsync's server twin lacks
        // position, MobService merges in the cached coords.
        using var f = NewFixture();
        // Pre-seed the KV with a resolved-position cache as if a
        // previous session had already raised + reconciled this MOB.
        var cacheJson = "[{\"ServerId\":\"recovered-id\","
                      + "\"Latitude\":47.5,\"Longitude\":8.5}]";
        await f.Kv.SetAsync("mob.resolvedPositions.v1", cacheJson);
        // The server's list returns the MOB but with no position
        // (the realistic shape - /mob discards POSt body position).
        f.Api.ListReturn = new Dictionary<string, ServerNotificationEnvelope>
        {
            ["recovered-id"] = new(
                Context: "vessels.self",
                Path: "notifications.mob.recovered-id",
                Value: new ServerNotificationDto(
                    Id: "recovered-id", State: "emergency", Message: "MOB",
                    Method: ["visual", "sound"],
                    Status: new NotificationStatusDto(false, false, false, true, true),
                    Position: null,
                    CreatedAt: DateTime.UtcNow)),
        };

        await f.Service.InitializeAsync();

        var entry = f.Store.Active.FirstOrDefault(n => n.Path == MobPathPrefix + "recovered-id");
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.Latitude).IsEqualTo(47.5);
        await Assert.That(entry.Longitude).IsEqualTo(8.5);
    }

    [Test]
    public async Task InitializeAsync_Skips_Non_Emergency_Entries()
    {
        // TEST-011: the state-filter ("any state=emergency") is
        // coarse but documented. Pin that a low-battery alarm
        // returned by the list endpoint does NOT enter the MOB
        // pipeline.
        using var f = NewFixture();
        f.Api.ListReturn = new Dictionary<string, ServerNotificationEnvelope>
        {
            ["low-batt"] = new(
                Context: "vessels.self",
                Path: "notifications.environment.battery.low",
                Value: new ServerNotificationDto(
                    Id: "low-batt", State: "alarm", Message: "Battery low",
                    Method: ["visual"],
                    Status: new NotificationStatusDto(false, false, false, true, true),
                    Position: null, CreatedAt: DateTime.UtcNow)),
        };

        await f.Service.InitializeAsync();

        await Assert.That(f.Store.Active.Any(n => n.Path.StartsWith(MobPathPrefix))).IsFalse();
    }

    [Test]
    public async Task InitializeAsync_Skips_Non_Mob_Emergencies()
    {
        // The state-filter catches "alarm"/"alert" but not a non-MOB
        // emergency (e.g. fire, flooding, vessel-aground). Without a
        // path-filter, those would get re-pathed under
        // notifications.mob.<id> and the MOB chart pipeline would try
        // to render them as casualties. Filter by env.Path so the MOB
        // pipeline only picks up genuine notifications.mob.* entries.
        using var f = NewFixture();
        f.Api.ListReturn = new Dictionary<string, ServerNotificationEnvelope>
        {
            ["fire-1"] = new(
                Context: "vessels.self",
                Path: "notifications.environment.fire",
                Value: new ServerNotificationDto(
                    Id: "fire-1", State: "emergency", Message: "Fire in engine room",
                    Method: ["visual", "sound"],
                    Status: new NotificationStatusDto(false, false, false, true, true),
                    Position: new NotificationPositionDto(47.5, 8.5),
                    CreatedAt: DateTime.UtcNow)),
        };

        await f.Service.InitializeAsync();

        await Assert.That(f.Store.Active.Any(n => n.Path.StartsWith(MobPathPrefix))).IsFalse();
    }

    [Test]
    public async Task InitializeAsync_Skips_Entries_With_Empty_Id()
    {
        // TEST-011: an emergency entry with no canonical id can't
        // produce a "notifications.mob." path; skip rather than
        // synthesise a path-with-empty-suffix.
        using var f = NewFixture();
        f.Api.ListReturn = new Dictionary<string, ServerNotificationEnvelope>
        {
            ["x"] = new(
                Context: "vessels.self",
                Path: "notifications.mob.x",
                Value: new ServerNotificationDto(
                    Id: "", State: "emergency", Message: "MOB",
                    Method: ["visual", "sound"],
                    Status: new NotificationStatusDto(false, false, false, true, true),
                    Position: null, CreatedAt: DateTime.UtcNow)),
        };

        await f.Service.InitializeAsync();

        await Assert.That(f.Store.Active.Any(n => n.Path.StartsWith(MobPathPrefix))).IsFalse();
    }

    // ------------------------------------------------------------
    // Clear paths (single + all)
    // ------------------------------------------------------------

    [Test]
    public async Task ClearAsync_Drops_Server_Side_Entry_And_Posts_Action_Verb()
    {
        // Server-twin entry (id known, no pending raise) clears via
        // POST /{id}/clear.
        using var f = NewFixture();
        f.Store.Apply("notifications.mob.bar", "emergency", "MOB",
            id: "bar",
            status: new NotificationStatus(false, false, false, true, true));

        var ok = await f.Service.ClearAsync("bar");

        await Assert.That(ok).IsTrue();
        await Assert.That(f.Store.Active.Any(n => n.Path == "notifications.mob.bar")).IsFalse();
        await Assert.That(f.Api.ClearCalls).Contains("bar");
    }

    [Test]
    public async Task ClearAsync_Returns_False_On_Api_Failure()
    {
        // TEST-003: the failure path on ClearAsync is part of the
        // contract - callers may want to surface a toast.
        using var f = NewFixture();
        f.Api.FailClearCount = 1;
        f.Store.Apply("notifications.mob.bar", "emergency", "MOB",
            id: "bar",
            status: new NotificationStatus(false, false, false, true, true));

        var ok = await f.Service.ClearAsync("bar");

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task ClearAsync_On_Pending_Local_Raise_Cancels_Retry_And_Skips_Post()
    {
        // SKEP-001 regression: clearing a pending offline MOB must
        // cancel its retry loop so a subsequent successful POST
        // doesn't resurrect the MOB via the WS echo. Also skips the
        // REST POST (no serverId yet -> would 404).
        using var f = NewFixture();
        f.Api.SuspendRaise = true;   // POST is parked indefinitely

        var localId = await f.Service.RaiseAsync("MOB", 47.5, 8.5);
        var path = MobPathPrefix + localId;
        await Assert.That(f.Store.Active.Any(n => n.Path == path)).IsTrue();
        // Synchronize on the background Task.Run actually entering
        // the gate before we mutate service state - otherwise
        // ClearAsync cancels the CTS before the loop has even
        // executed its first line and no API call gets recorded.
        await f.Api.WaitForSuspendedRaiseAsync();

        var ok = await f.Service.ClearAsync(localId);

        await Assert.That(ok).IsTrue();
        await Assert.That(f.Store.Active.Any(n => n.Path == path)).IsFalse();
        // No REST clear fired - no serverId existed.
        await Assert.That(f.Api.ClearCalls.Count).IsEqualTo(0);
        // Retry loop must have been cancelled. Releasing the
        // SuspendRaise gate here would let any leaked loop fire a
        // POST; a leaked loop fails the assertion below.
        f.Api.SuspendRaise = false;
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        await Task.Delay(50);   // give a hypothetical leaked task a chance
        await Assert.That(f.Api.RaiseMobCalls.Count).IsEqualTo(1);
        // The single call recorded is the one suspended at the time
        // of cancel; no successful follow-up.
    }

    [Test]
    public async Task ClearAllAsync_Tears_Down_Pending_And_Server_Mobs()
    {
        // The bar-button two-tap path. Pending + server entries
        // both cleared in one call.
        using var f = NewFixture();
        f.Api.SuspendRaise = true;
        var localId = await f.Service.RaiseAsync("MOB pending", 47.5, 8.5);
        f.Store.Apply("notifications.mob.server-foo", "emergency", "MOB server",
            id: "server-foo",
            status: new NotificationStatus(false, false, false, true, true));

        await f.Service.ClearAllAsync();

        var mobPaths = f.Store.Active
            .Where(n => n.Path.StartsWith(MobPathPrefix, StringComparison.Ordinal))
            .ToList();
        await Assert.That(mobPaths.Count).IsEqualTo(0);
        // The server entry was cleared via REST; the pending one
        // wasn't (no serverId).
        await Assert.That(f.Api.ClearCalls).Contains("server-foo");
        await Assert.That(f.Api.ClearCalls.Count).IsEqualTo(1);
    }

    // ------------------------------------------------------------
    // Dispose contract (TEST-007)
    // ------------------------------------------------------------

    [Test]
    public async Task Dispose_Cancels_Pending_Retry_Loops()
    {
        // TEST-007: leaks of Task.Run are silent in tests; pin that
        // dispose actually cancels.
        using var f = NewFixture();
        f.Api.FailRaiseCount = 1000;   // never succeed
        f.Api.SuspendRaise = false;     // let the first attempt fly
        var localId = await f.Service.RaiseAsync("MOB", 47.5, 8.5);
        // Spin-wait until the first POST is recorded - the loop's
        // first iteration is unconditional (no backoff). Awaiting
        // the loop Task itself would hang forever because attempts
        // 2+ are gated on Task.Delay against FakeTimeProvider, which
        // we never advance in this test.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (f.Api.RaiseMobCalls.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        await Assert.That(f.Api.RaiseMobCalls.Count).IsGreaterThanOrEqualTo(1);
        // Loop is now parked on the 1s backoff awaiting the next tick.
        var atDispose = f.Api.RaiseMobCalls.Count;

        f.Service.Dispose();

        // Advance well past the backoff schedule. A leaked loop
        // would fire more attempts.
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(20);
        await Assert.That(f.Api.RaiseMobCalls.Count).IsEqualTo(atDispose);
    }

    [Test]
    public async Task Dispose_Unsubscribes_From_Store_OnPathChanged()
    {
        // TEST-007: a disposed service should not respond to store
        // events. Asserts no exception + no resurrection of state.
        using var f = NewFixture();
        f.Service.Dispose();

        f.Store.Apply("notifications.mob.x", "emergency", "MOB",
            id: "x",
            status: new NotificationStatus(false, false, false, true, true));

        // No throw, no rehydrate. Store still has the entry (the
        // Apply itself works); we just check the service didn't
        // crash on a now-empty _pending dict.
        await Assert.That(f.Store.Active.Any(n => n.Path == "notifications.mob.x")).IsTrue();
    }

    // ------------------------------------------------------------
    // Test fakes
    // ------------------------------------------------------------

    /// <summary>Shared in-memory KV fake.</summary>
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
    /// id, suspend the response, or fail N times before succeeding.</summary>
    private sealed class FakeNotificationsApi : INotificationsApi
    {
        public List<string?> RaiseMobCalls { get; } = [];
        public List<string> AckCalls { get; } = [];
        public List<string> ClearCalls { get; } = [];
        public string NextServerId { get; set; } = "server-id-1";
        public int FailRaiseCount { get; set; } = 0;
        public int FailClearCount { get; set; } = 0;
        /// <summary>When non-null, RaiseMobAsync parks on this TCS
        /// until it's resolved or the call's CancellationToken
        /// cancels. Lets a test observe the local-first synthetic
        /// without racing the background POST. Fixture.Dispose
        /// cancels the call's ct so a still-parked attempt unwinds
        /// promptly without leaking a Task.Delay timer.</summary>
        private TaskCompletionSource? _suspendGate;
        /// <summary>Companion to <see cref="_suspendGate"/>: signals
        /// when the suspended call has actually entered the gate.
        /// Tests use <see cref="WaitForSuspendedRaiseAsync"/> to
        /// synchronize "the background Task.Run has reached the API
        /// call" before mutating service state. Without this the
        /// thread-pool scheduler can leave Task.Run unstarted and
        /// a subsequent ClearAsync cancels the CTS before the loop
        /// ever ran - making the call count off-by-one.</summary>
        private TaskCompletionSource? _raiseEnteredGate;
        public bool SuspendRaise
        {
            get => _suspendGate is not null;
            set
            {
                if (value)
                {
                    _suspendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _raiseEnteredGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else
                {
                    // Release any parked call so its gate.Task.WaitAsync
                    // completes - otherwise the loop stays suspended
                    // forever even after a test sets SuspendRaise=false.
                    _suspendGate?.TrySetResult();
                    _suspendGate = null;
                    _raiseEnteredGate = null;
                }
            }
        }

        /// <summary>Awaitable that completes once the next
        /// RaiseMobAsync call has hit the suspend gate. Returns
        /// completed task when SuspendRaise is false.</summary>
        public Task WaitForSuspendedRaiseAsync() =>
            _raiseEnteredGate?.Task ?? Task.CompletedTask;
        public IReadOnlyDictionary<string, ServerNotificationEnvelope>? ListReturn { get; set; }

        public async Task<ApiResult<string>> RaiseMobAsync(string? message, CancellationToken ct = default)
        {
            RaiseMobCalls.Add(message);
            if (_suspendGate is { } gate)
            {
                // Signal that we've entered the gate so the test
                // can stop racing on Task.Run scheduling.
                _raiseEnteredGate?.TrySetResult();
                // WaitAsync(ct) throws OCE on cancellation - exactly
                // what the production retry loop needs to break out
                // when the service disposes.
                try { await gate.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { return ApiResult<string>.Fail("cancelled"); }
            }
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

        public Task<IReadOnlyDictionary<string, ServerNotificationEnvelope>?> ListActiveAsync(CancellationToken ct = default)
            => Task.FromResult(ListReturn);

        // Path-keyed verbs not exercised by MOB tests (alarm publisher
        // uses them); stubbed for interface compliance.
        public Task<ApiResult> SilenceAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult<string>> RaiseAsync(string path, NotificationPayload body, CancellationToken ct = default)
            => Task.FromResult(ApiResult<string>.Ok("path-id"));
        // ClearAsync (DELETE /<id>) is the actual clear path the MOB
        // pipeline calls. Records the id and fails N times when
        // FailClearCount is set, so tests pin both the verb and the
        // surface for "POST returned 4xx" recovery.
        public Task<ApiResult> ClearAsync(string id, CancellationToken ct = default)
        {
            ClearCalls.Add(id);
            if (FailClearCount > 0)
            {
                FailClearCount--;
                return Task.FromResult(ApiResult.Fail("network"));
            }
            return Task.FromResult(ApiResult.Ok);
        }
    }
}
