using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

public class AisStoreTests
{
    [Test]
    public async Task Empty_CountIsZero()
    {
        var store = new AisStore();
        await Assert.That(store.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Empty_GetVessels_ReturnsEmpty()
    {
        var store = new AisStore();
        await Assert.That(store.GetVessels()).IsEmpty();
    }

    [Test]
    public async Task Apply_NewContext_CreatesVessel()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.position", pos);
        await Assert.That(store.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SetName_WhenEmpty_PopulatesAndFiresEvent()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:222222222", "navigation.position", pos);
        int fired = 0;
        store.OnAisUpdated += () => fired++;

        store.SetName("vessels.urn:mrn:imo:mmsi:222222222", "MARCO POLO");

        await Assert.That(store.GetVessels()[0].Name).IsEqualTo("MARCO POLO");
        await Assert.That(fired).IsEqualTo(1);
    }

    [Test]
    public async Task SetName_WhenAlreadySet_DoesNotOverwrite()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:333333333", "navigation.position", pos);
        var nameEl = JsonSerializer.SerializeToElement("ORIGINAL");
        store.Apply("vessels.urn:mrn:imo:mmsi:333333333", "name", nameEl);

        store.SetName("vessels.urn:mrn:imo:mmsi:333333333", "OVERRIDE");

        await Assert.That(store.GetVessels()[0].Name).IsEqualTo("ORIGINAL");
    }

    [Test]
    public async Task SetName_UnknownContext_NoOp()
    {
        var store = new AisStore();
        store.SetName("vessels.urn:mrn:imo:mmsi:444444444", "NOBODY");
        await Assert.That(store.Count).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateBuddies_TagsExistingVessels()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111", "navigation.position", pos);
        store.Apply("vessels.urn:mrn:imo:mmsi:222", "navigation.position", pos);

        store.UpdateBuddies(["vessels.urn:mrn:imo:mmsi:111"]);

        var vessels = store.GetVessels();
        var buddy = vessels.First(v => v.Context.EndsWith("111"));
        var other = vessels.First(v => v.Context.EndsWith("222"));
        await Assert.That(buddy.IsBuddy).IsTrue();
        await Assert.That(other.IsBuddy).IsFalse();
    }

    [Test]
    public async Task UpdateBuddies_SeedsVesselsCreatedLater()
    {
        var store = new AisStore();
        store.UpdateBuddies(["vessels.urn:mrn:imo:mmsi:333"]);

        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:333", "navigation.position", pos);
        store.Apply("vessels.urn:mrn:imo:mmsi:444", "navigation.position", pos);

        var vessels = store.GetVessels();
        await Assert.That(vessels.First(v => v.Context.EndsWith("333")).IsBuddy).IsTrue();
        await Assert.That(vessels.First(v => v.Context.EndsWith("444")).IsBuddy).IsFalse();
    }

    [Test]
    public async Task UpdateBuddies_RemovesBuddyFlag()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:555", "navigation.position", pos);
        store.UpdateBuddies(["vessels.urn:mrn:imo:mmsi:555"]);
        await Assert.That(store.GetVessels()[0].IsBuddy).IsTrue();

        // User removed the buddy in the plugin UI; refreshing with an empty
        // list must un-tag the vessel.
        store.UpdateBuddies([]);
        await Assert.That(store.GetVessels()[0].IsBuddy).IsFalse();
    }

    [Test]
    public async Task RadarContext_SeedsRadarSource_AndRdrName()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("radar.1.T42", "position", pos);

        var v = store.GetVessels().Single();
        await Assert.That(v.Source).IsEqualTo(TargetSource.Radar);
        await Assert.That(v.Mmsi).IsNull();
        await Assert.That(v.Name).IsEqualTo("RDR-T42");
        await Assert.That(v.Latitude).IsEqualTo(47.0);
        await Assert.That(v.Longitude).IsEqualTo(8.0);
    }

    [Test]
    public async Task RadarTarget_AcceptsCourseAndSpeedShorthand()
    {
        var store = new AisStore();
        store.Apply("radar.1.T1", "position",
            JsonSerializer.SerializeToElement(new { latitude = 0.0, longitude = 0.0 }));
        store.Apply("radar.1.T1", "course", JsonSerializer.SerializeToElement(1.57));
        store.Apply("radar.1.T1", "speed", JsonSerializer.SerializeToElement(4.1));

        var v = store.GetVessels().Single();
        await Assert.That(v.CourseOverGround).IsEqualTo(1.57);
        await Assert.That(v.SpeedOverGround).IsEqualTo(4.1);
    }

    [Test]
    public async Task AisContext_KeepsAisSource()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 1.0, longitude = 2.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:777777777", "navigation.position", pos);

        var v = store.GetVessels().Single();
        await Assert.That(v.Source).IsEqualTo(TargetSource.Ais);
        await Assert.That(v.Mmsi).IsEqualTo("777777777");
    }

    [Test]
    public async Task EmptyPath_IdentityObject_ExtractsNameAndMmsi()
    {
        // SignalK servers publish AIS message type 5 (static vessel data)
        // as { path: "", value: { name, mmsi, ... } } - a bulk identity
        // snapshot. The previous flat-path switch didn't handle empty
        // paths, so vessels in that scenario showed only a MMSI label
        // on the chart (the "Ship names sometimes missing" bug). This
        // test pins the fan-out behaviour so a future refactor can't
        // silently regress back to the flat lookup.
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:888", "navigation.position", pos);

        var identity = JsonSerializer.SerializeToElement(new
        {
            name = "SALTY BREEZE",
            mmsi = "888",
            communication = new { callsignVhf = "DEV01" }
        });
        store.Apply("vessels.urn:mrn:imo:mmsi:888", "", identity);

        var v = store.GetVessels().Single();
        await Assert.That(v.Name).IsEqualTo("SALTY BREEZE");
        await Assert.That(v.Mmsi).IsEqualTo("888");
        await Assert.That(v.Callsign).IsEqualTo("DEV01");
    }

    [Test]
    public async Task AisVessel_BuddyPathApplied()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:666", "navigation.position", pos);

        // Live update via the delta stream: the plugin pushes buddy=true.
        var buddyTrue = JsonSerializer.SerializeToElement(true);
        store.Apply("vessels.urn:mrn:imo:mmsi:666", "buddy", buddyTrue);

        await Assert.That(store.GetVessels()[0].IsBuddy).IsTrue();
    }

    [Test]
    public async Task Apply_SameContext_UpdatesExistingVessel()
    {
        var store = new AisStore();
        var ctx = "vessels.urn:mrn:imo:mmsi:222222222";
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply(ctx, "navigation.position", pos);

        var name = JsonSerializer.SerializeToElement("TestVessel");
        store.Apply(ctx, "name", name);

        await Assert.That(store.Count).IsEqualTo(1);
        var vessels = store.GetVessels();
        await Assert.That(vessels.Length).IsEqualTo(1);
        await Assert.That(vessels[0].Name).IsEqualTo("TestVessel");
    }

    [Test]
    public async Task GetVessels_OnlyReturnsVesselsWithPosition()
    {
        var store = new AisStore();
        var ctx1 = "vessels.urn:mrn:imo:mmsi:111111111";
        var ctx2 = "vessels.urn:mrn:imo:mmsi:222222222";

        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply(ctx1, "navigation.position", pos);

        var name = JsonSerializer.SerializeToElement("NoPosition");
        store.Apply(ctx2, "name", name);

        var vessels = store.GetVessels();
        await Assert.That(vessels.Length).IsEqualTo(1);
        await Assert.That(vessels[0].Context).IsEqualTo(ctx1);
    }

    [Test]
    public async Task Apply_ExtractsMmsiFromContext()
    {
        var store = new AisStore();
        var ctx = "vessels.urn:mrn:imo:mmsi:333333333";
        var pos = JsonSerializer.SerializeToElement(new { latitude = 1.0, longitude = 2.0 });
        store.Apply(ctx, "navigation.position", pos);

        var vessels = store.GetVessels();
        await Assert.That(vessels[0].Mmsi).IsEqualTo("333333333");
    }

    [Test]
    public async Task GetVessels_ReturnsCachedSnapshot()
    {
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.position", pos);

        var snap1 = store.GetVessels();
        var snap2 = store.GetVessels();

        await Assert.That(snap2).IsSameReferenceAs(snap1);
    }

    [Test]
    public async Task GetVessels_RefreshesCacheAfterUpdate()
    {
        var store = new AisStore();
        var pos1 = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.position", pos1);
        var snap1 = store.GetVessels();

        var sog = JsonSerializer.SerializeToElement(3.5);
        store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.speedOverGround", sog);
        var snap2 = store.GetVessels();

        await Assert.That(snap2).IsNotSameReferenceAs(snap1);
    }

    [Test]
    public async Task Apply_RaisesOnAisUpdated()
    {
        var store = new AisStore();
        int raised = 0;
        store.OnAisUpdated += () => raised++;

        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111111111", "navigation.position", pos);

        await Assert.That(raised).IsEqualTo(1);
    }

    [Test]
    public async Task Evict_Removes_And_Blocks_Re_Add()
    {
        // The "self arrived late" scenario: own-boat was stored as AIS
        // before SignalkClient learned the self URN. Evict must drop the
        // entry AND ensure subsequent deltas on that context don't
        // re-create it.
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        const string self = "vessels.urn:mrn:imo:mmsi:999999999";

        store.Apply(self, "navigation.position", pos);
        await Assert.That(store.Count).IsEqualTo(1);

        store.Evict(self);
        await Assert.That(store.Count).IsEqualTo(0);

        // Further deltas on the evicted context must be ignored.
        store.Apply(self, "navigation.position", pos);
        store.Apply(self, "name", JsonSerializer.SerializeToElement("Own Boat"));
        await Assert.That(store.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Evict_Fires_OnAisUpdated_For_Map_Refresh()
    {
        // Removal must push the vessel list to subscribers; otherwise the
        // Map page keeps rendering the ghost marker for own-boat-as-AIS
        // until another vessel update lands.
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:foo", "navigation.position", pos);

        int raised = 0;
        store.OnAisUpdated += () => raised++;
        store.Evict("vessels.urn:foo");

        await Assert.That(raised).IsEqualTo(1);
    }

    [Test]
    public async Task RemoveContext_DoesNotBlocklist_ReAcceptsLaterDelta()
    {
        // Radar tracking ends and SignalkClient calls RemoveContext to
        // clear the target. The same target id can come back later (a
        // dropped contact reacquired) - unlike Evict, RemoveContext
        // must NOT add the context to the blocklist or the new delta
        // is silently dropped and the operator sees the target vanish
        // from the chart.
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        const string radarCtx = "radar.42.t7";

        store.Apply(radarCtx, "navigation.position", pos);
        await Assert.That(store.Count).IsEqualTo(1);

        store.RemoveContext(radarCtx);
        await Assert.That(store.Count).IsEqualTo(0);

        // Next delta on the same context must be accepted.
        store.Apply(radarCtx, "navigation.position", pos);
        await Assert.That(store.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RemoveContext_OnUnknownContext_NoOp()
    {
        // Defensive: a stray cleanup call on a context that was never
        // tracked (or was already removed) must not throw, allocate, or
        // fire OnAisUpdated.
        var store = new AisStore();
        int raised = 0;
        store.OnAisUpdated += () => raised++;

        store.RemoveContext("radar.99.unknown");

        await Assert.That(store.Count).IsEqualTo(0);
        await Assert.That(raised).IsEqualTo(0);
    }

    [Test]
    public async Task RemoveContext_NullOrEmpty_NoOp()
    {
        // Wire-protocol robustness: the dispatcher might pass an empty
        // context if a delta is malformed. Same expectation as the
        // SetName / Evict guards.
        var store = new AisStore();
        int raised = 0;
        store.OnAisUpdated += () => raised++;

        store.RemoveContext("");
        store.RemoveContext(null!);

        await Assert.That(raised).IsEqualTo(0);
    }

    [Test]
    public async Task GetVessels_AfterApplyWithoutPosition_FiltersToPositional()
    {
        // Snapshot rebuild must filter on .Latitude/.Longitude. A vessel
        // whose only delta was a name (AIS msg type 5 ahead of any 1/3/4)
        // should NOT appear in the rendered list - it would draw at
        // (0,0) off the African coast otherwise.
        var store = new AisStore();
        store.Apply("vessels.urn:mrn:imo:mmsi:1", "name",
            JsonSerializer.SerializeToElement("NAMED BUT NO POSITION"));
        store.Apply("vessels.urn:mrn:imo:mmsi:2", "navigation.position",
            JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 }));

        var snapshot = store.GetVessels();
        await Assert.That(snapshot.Length).IsEqualTo(1);
        await Assert.That(snapshot[0].Mmsi).IsEqualTo("2");
    }

    // --- Stale eviction: long-running session must not leak vessels ---

    [Test]
    public async Task GetVessels_AfterLongIdle_PrunesStaleEntries()
    {
        // Boats sitting at anchor in port watch the AIS list grow for
        // hours. The store evicts entries whose LastSeen is older than
        // 10 min during a throttled prune (every 2 min). A vessel that
        // hasn't been seen for 15 min must drop off the snapshot.
        //
        // Reflection drives the LastSeen field directly because the
        // wire-side interface only sets it through Apply() and we
        // can't move the wall clock back to "12 minutes ago" without
        // either real waits or model changes. The reflection is
        // brittle by design: a rename should fail this test loudly so
        // the prune contract gets an explicit follow-up.
        var store = new AisStore();
        var pos = System.Text.Json.JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:stale-1", "navigation.position", pos);
        store.Apply("vessels.urn:mrn:imo:mmsi:stale-2", "navigation.position", pos);
        store.Apply("vessels.urn:mrn:imo:mmsi:fresh-1", "navigation.position", pos);

        // Backdate two of the three vessels. LastSeen is a public
        // setter so we can mutate without reflection. The store also
        // keeps a private _lastPruneTime gating the 2-min throttle;
        // we nudge that via reflection so the next GetVessels triggers
        // a prune sweep.
        var staleCutoff = DateTime.UtcNow.AddMinutes(-15);
        foreach (var v in store.GetVessels())
        {
            if (v.Context.Contains("stale"))
            {
                v.LastSeen = staleCutoff;
            }
        }

        // Reset the throttle so the next GetVessels triggers a prune.
        var pruneField = typeof(AisStore).GetField(
            "_lastPruneTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        pruneField.SetValue(store, DateTime.UtcNow.AddMinutes(-3));

        var snapshot = store.GetVessels();
        await Assert.That(snapshot.Length).IsEqualTo(1);
        await Assert.That(snapshot[0].Context).IsEqualTo("vessels.urn:mrn:imo:mmsi:fresh-1");
    }

    [Test]
    public async Task UpdateBuddies_NoChange_DoesNotFire()
    {
        // Calling UpdateBuddies with the same set as last time must not
        // refire OnAisUpdated - the observer would otherwise repaint
        // the entire vessel list on every settings tick. Boundary case
        // for the "if (changed)" guard.
        var store = new AisStore();
        var pos = System.Text.Json.JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:bud-1", "navigation.position", pos);
        store.UpdateBuddies(["vessels.urn:mrn:imo:mmsi:bud-1"]);

        int fires = 0;
        store.OnAisUpdated += () => fires++;

        // Same set again - the buddy flag for bud-1 is already true,
        // so no flip happens and the event must not fire.
        store.UpdateBuddies(["vessels.urn:mrn:imo:mmsi:bud-1"]);
        await Assert.That(fires).IsEqualTo(0);
    }

    [Test]
    public async Task Evict_NullOrEmpty_NoOp()
    {
        // Wire-protocol robustness: a malformed delta could feed an
        // empty context here. The guard mirrors RemoveContext and
        // SetName - early-return so no event fires and no spurious
        // blocklist entry is added.
        var store = new AisStore();
        int fires = 0;
        store.OnAisUpdated += () => fires++;

        store.Evict("");
        store.Evict(null!);

        await Assert.That(fires).IsEqualTo(0);
        await Assert.That(store.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetVessels_ConcurrentApplyAndRead_NoCorruptionAndEventualConsistency()
    {
        // Race scenario: WebSocket thread is firing Apply() while the UI
        // thread reads GetVessels(). The cache rebuild must never throw
        // (e.g. mid-rebuild dictionary mutation) and the final read must
        // contain every vessel that finished applying. Tests the Volatile
        // version-check + ConcurrentDictionary contract together.
        var store = new AisStore();
        const int writers = 4;
        const int perWriter = 200;
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        var stopReads = false;

        var writeTasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < perWriter; i++)
                store.Apply($"vessels.urn:mrn:imo:mmsi:{w}-{i}",
                    "navigation.position", pos);
        })).ToArray();

        // Reader hammers GetVessels concurrently; failure mode would be
        // an exception bubbling out (collection-modified) or a snapshot
        // missing items that a subsequent read still doesn't show.
        var readTask = Task.Run(() =>
        {
            int observedMax = 0;
            while (!stopReads)
            {
                int n = store.GetVessels().Length;
                if (n > observedMax) observedMax = n;
            }
            return observedMax;
        });

        await Task.WhenAll(writeTasks);
        stopReads = true;
        _ = await readTask;

        // After all writers finished, the next snapshot must include
        // every vessel.
        await Assert.That(store.GetVessels().Length).IsEqualTo(writers * perWriter);
    }
}
