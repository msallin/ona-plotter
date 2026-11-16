using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Js;
using OnaPlotter.Services.Map;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the contract of <see cref="AisPushService"/> on the surface
/// area Map.razor used to own: payload-shape (the JS layer expects
/// specific field names), harbor-mode filter pass-through, and the
/// "no map yet" guard. The detailed CPA / COLREGS / palette /
/// harbour math has its own dedicated test suites.
/// </summary>
public class AisPushServiceTests
{
    private sealed class FakeAisJs : IMapAisJs
    {
        public List<AisUpdatePayload> Updates { get; } = [];
        /// <summary>Renderer answers; true (accepted) once exhausted.</summary>
        public Queue<bool> Answers { get; } = new();

        /// <summary>Vessels of the Nth accepted-or-not update.</summary>
        public AisVesselPayload[] V(int n) => Updates[n].Vessels;

        public Task<bool> UpdateAisTargetsAsync(AisUpdatePayload update)
        {
            Updates.Add(update);
            return Task.FromResult(Answers.Count == 0 || Answers.Dequeue());
        }

        public Task SetAtonsAsync(AtonPayload[] atons) => Task.CompletedTask;
        public Task SetAtonsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetAisLabelsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetOwnMmsiAsync(string mmsi) => Task.CompletedTask;
        public Task SetOwnCallsignAsync(string callsign) => Task.CompletedTask;
        public Task SetHarborModeAsync(bool enabled) => Task.CompletedTask;
        public Task<bool> FocusVesselAsync(string context) => Task.FromResult(false);
    }

    /// <summary>Mutable time provider for the harbor-mode dwell test.
    /// Avoids dragging in <c>Microsoft.Extensions.Time.Testing</c> for
    /// a single Advance() call.</summary>
    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTime(DateTime utc) { _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)); }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private static AisPushService NewService(
        AisStore store,
        IMapAisJs js,
        IAppSettings? settings = null,
        IMooredVesselTracker? tracker = null,
        TimeProvider? time = null) =>
        new(js, store, tracker ?? new MooredVesselTracker(), settings ?? new FakeSettings(),
            time ?? new FakeTime(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)));

    private static void SeedVessel(AisStore store, string context, double lat, double lon,
        double? sog = 5.0, double? cog = 1.5, string? name = null)
    {
        var pos = JsonSerializer.SerializeToElement(new { latitude = lat, longitude = lon });
        store.Apply(context, "navigation.position", pos);
        if (sog is double s) store.Apply(context, "navigation.speedOverGround", s);
        if (cog is double c) store.Apply(context, "navigation.courseOverGroundTrue", c);
        if (name is not null) store.Apply(context, "name", name);
    }

    [Test]
    public async Task Push_With_No_Vessels_Sends_Empty_Array()
    {
        var store = new AisStore();
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(new NavigationData());

        await Assert.That(js.Updates.Count).IsEqualTo(1);
        await Assert.That(js.V(0).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Push_Skips_Vessels_Without_Position()
    {
        // AisStore returns vessels with positions only, so a vessel
        // delta that arrives without lat/lon never makes it through.
        // This mirrors HarborAisFilter's bedrock guarantee.
        var store = new AisStore();
        store.Apply("vessels.urn:mrn:imo:mmsi:111", "name", "Ghost");
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:222", 47.0, 8.0, name: "Real");
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(new NavigationData());

        await Assert.That(js.V(0).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Harbor_Mode_Drops_Moored_Vessels()
    {
        // Settings.HarborMode flag enables the moored-vessel filter via
        // HarborAisFilter; a low-SOG vessel after the dwell window
        // should disappear from the snapshot.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, sog: 0.05);
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:222", 47.1, 8.1, sog: 5.0);

        var settings = new FakeSettings { HarborMode = true };
        var time = new FakeTime(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var tracker = new MooredVesselTracker();
        var js = new FakeAisJs();
        var svc = NewService(store, js, settings, tracker, time);

        await svc.PushAsync(new NavigationData());
        time.Advance(TimeSpan.FromSeconds(75));      // past dwell
        await svc.PushAsync(new NavigationData());

        // The moored vessel drops on the second push; the active one
        // stays and, being unchanged, isn't resent.
        await Assert.That(js.Updates[1].Removed).IsEquivalentTo(new[] { "vessels.urn:mrn:imo:mmsi:111" });
        await Assert.That(js.V(1).Any(v => v.Context.EndsWith("222"))).IsFalse();
    }

    [Test]
    public async Task DisplayName_Transitions_From_Mmsi_To_Name_When_Name_Arrives()
    {
        // Helm-feedback: an AIS marker that initially shows the MMSI in
        // its on-chart label is expected to flip to the vessel name once
        // AIS Type 5 / 24 static delivers it on a subsequent delta. This
        // pins the C# side of that transition - if `name` later stops
        // overriding `mmsi` in the displayName fallback chain (rename,
        // bug, future refactor), this test catches it before the JS
        // layer sees the broken payload.
        var store = new AisStore();
        const string ctx = "vessels.urn:mrn:imo:mmsi:211234567";

        // Tick 1: position-only, no name yet (typical first delta for an
        // AIS target before the static message arrives).
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply(ctx, "navigation.position", pos);
        var js = new FakeAisJs();
        var svc = NewService(store, js);
        await svc.PushAsync(new NavigationData());
        var firstName = js.V(0)[0].Static!.DisplayName;

        // Tick 2: name delta arrives (AIS Type 5 static). MUST override
        // the MMSI fallback on the next push.
        store.Apply(ctx, "name", "Salty Breeze");
        await svc.PushAsync(new NavigationData());
        var secondName = js.V(1)[0].Static!.DisplayName;

        await Assert.That(firstName).IsEqualTo("211234567");
        await Assert.That(secondName).IsEqualTo("Salty Breeze");
    }

    [Test]
    public async Task Payload_Has_The_Expected_Shape_For_JS_Renderer()
    {
        // The JS side reads specific property names off each entry; if
        // the contract drifts, markers vanish silently. Walk one entry
        // via reflection so a renamed field shouts at compile time when
        // someone adds an enum entry.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, name: "Test Boat");
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(new NavigationData());

        // Spot-check a representative subset of the fields the JS side
        // expects. With the typed AisVesselPayload these become direct
        // property reads instead of reflection probes; the property
        // existence is enforced at compile time. The wire-key contract
        // (camelCase) is enforced separately by the [JsonPropertyName]
        // annotations on the type itself.
        var entry = js.V(0)[0];
        await Assert.That(entry.Context).IsEqualTo("vessels.urn:mrn:imo:mmsi:111");
        await Assert.That(entry.Lat).IsEqualTo(47.0);
        await Assert.That(entry.Lon).IsEqualTo(8.0);
        await Assert.That(entry.Static!.DisplayName).IsEqualTo("Test Boat");
        await Assert.That(entry.Static.Title).IsEqualTo("Test Boat");
        await Assert.That(entry.Static.Radar).IsFalse();
        await Assert.That(entry.CpaThreat).IsEqualTo("none");
        await Assert.That(entry.MarkerTone).IsEqualTo(AisTone.Ship);
        await Assert.That(entry.LabelVisible).IsTrue();
    }

    [Test]
    public async Task ChipClassifier_Anchored_NarrowsToAnchorRadius()
    {
        // Helm-flagged regression: visible guard-zone rings narrow
        // to the anchor swing radius (e.g. 0.05 nm) when the SK
        // anchoralarm-plugin is active, but the chart-side chip
        // classifier was still using the underway threshold (0.5 nm
        // default), so amber chips appeared OUTSIDE the visible
        // rings. Pin: when anchored with a 30 m max-radius, a vessel
        // with CPA = 0.09 nm gets cpaThreat = "none" (not "warning"),
        // because 0.09 nm > anchor-narrowed warning band of
        // ~0.0162 * 2 = ~0.032 nm.
        var ownLat = 47.0; var ownLon = 8.0;
        // Place the AIS target ahead of own boat at a distance whose
        // CPA works out to ~0.09 nm given matching headings.
        // 0.0015 deg lat = ~0.09 nm. Same lon so target is straight
        // ahead. Closing speed: own going north, target going south.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", ownLat + 0.0015, ownLon,
            sog: 5.0, cog: Math.PI);  // south-bound
        var js = new FakeAisJs();

        // Anchored own-boat with a tight 30 m radius.
        var nav = new NavigationData();
        nav.Apply("navigation.position", JsonSerializer.SerializeToElement(
            new { latitude = ownLat, longitude = ownLon }));
        nav.Apply("navigation.speedOverGround", 0.05);          // basically still
        nav.Apply("navigation.courseOverGroundTrue", 0.0);
        nav.ApplyAnchorPosition(ownLat, ownLon);
        nav.Apply("navigation.anchor.maxRadius", 30.0);

        var settings = new FakeSettings
        {
            CpaAlarmNm = 0.5,
            TcpaAlarmMin = 30.0,
            CpaAwarenessNm = 1.0,
            TcpaAwarenessMin = 30.0,
        };
        var svc = NewService(store, js, settings);

        await svc.PushAsync(nav);

        var entry = js.V(0)[0];
        // 0.09 nm CPA is OUTSIDE the anchor-narrowed band (both tiers
        // collapse to ~0.0162 nm at anchor) - the chip should be
        // classified None. Without the anchor-narrowing the underway
        // awareness band (1.0 nm) would have classified this awareness.
        await Assert.That(entry.CpaThreat)
            .IsEqualTo("none")
            .Because("anchored chip classifier must use the narrowed " +
                     "anchor radius so chips don't appear outside the " +
                     "visible guard-zone rings");
    }

    [Test]
    public async Task Buddy_Vessels_Get_Star_Prefix()
    {
        // displayName = "★ NAME" when the vessel is on the buddy list.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, name: "Friend");
        store.UpdateBuddies(new[] { "vessels.urn:mrn:imo:mmsi:111" });
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(new NavigationData());

        var entry = js.V(0)[0];
        await Assert.That(entry.Static!.DisplayName).IsEqualTo("★ Friend");
        await Assert.That(entry.Static.Title).IsEqualTo("★ Friend");
        await Assert.That(entry.MarkerTone).IsEqualTo(AisTone.Buddy);
        await Assert.That(entry.VectorTone).IsEqualTo(AisTone.Buddy);
    }

    // --- BuildSnapshot resilience pins (PR #264) ----------------------
    //
    // Hardening against the failure modes the code-review flagged:
    // a) NaN/Infinity slipping through AisStore poisoning JSInterop
    // b) a single bad vessel record blowing up the whole tick
    // c) the cache committing before the work succeeds, swallowing
    //    failed pushes and freezing the map on stale data
    // The failing JS-side push (rare but real - radar-overlay leak)
    // emptied the chart for the rest of the session before this fix.

    [Test]
    public async Task BuildSnapshot_Skips_Vessel_With_NaN_Position_DoesNotPoisonOthers()
    {
        // AisStore.GetVessels filters null lat/lon, but a NaN slipping
        // through (manually-crafted delta, future schema migration)
        // used to propagate to JSInterop which throws "Cannot serialize
        // NaN to JSON" and dumped the entire snapshot. The per-vessel
        // finite gate skips the offender and keeps the rest.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, name: "Real");
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:222", 47.5, 8.5, name: "BadOne");
        // Manually corrupt the bad vessel's coords post-seed. AisStore's
        // ConcurrentDictionary exposes the live AisVessel instance via
        // GetVessels; we mutate it to simulate a NaN that slipped past
        // the nullable filter.
        var bad = store.GetVessels().First(v => v.Context.EndsWith("222"));
        bad.Latitude = double.NaN;

        var js = new FakeAisJs();
        var svc = NewService(store, js);
        await svc.PushAsync(new NavigationData());

        // Only the good vessel survives.
        await Assert.That(js.V(0).Length).IsEqualTo(1);
        await Assert.That(js.V(0)[0].Context).IsEqualTo("vessels.urn:mrn:imo:mmsi:111");
    }

    [Test]
    public async Task BuildSnapshot_Skips_Vessel_With_Infinity_Sog()
    {
        // Same defensive shape on SOG. NaN/Infinity in COG/SOG would
        // flow into Cpa.Compute (which guards) but ALSO into the
        // anonymous-payload write that JSInterop serialises - which
        // doesn't.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, name: "Real");
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:222", 47.5, 8.5, name: "BadSog");
        var bad = store.GetVessels().First(v => v.Context.EndsWith("222"));
        bad.SpeedOverGround = double.PositiveInfinity;

        var js = new FakeAisJs();
        var svc = NewService(store, js);
        await svc.PushAsync(new NavigationData());

        await Assert.That(js.V(0).Length).IsEqualTo(1);
        await Assert.That(js.V(0)[0].Context).IsEqualTo("vessels.urn:mrn:imo:mmsi:111");
    }

    [Test]
    public async Task PushAsync_OnInteropFailure_DoesNotCommitCache_NextTickRetries()
    {
        // Cache-commit ordering: previously the skip-when-unchanged
        // cache (_lastPushed*) was written BEFORE the JS interop call.
        // If the interop call threw, the next tick would skip
        // (because the cache said "we already pushed this state") and
        // the map would freeze on stale data until something else
        // changed. Pin: a failed first push leaves the cache in the
        // pre-push state so the next tick retries.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0);
        var js = new ThrowOnceAisJs();
        var svc = NewService(store, js);
        var nav = new NavigationData();
        nav.ApplyPosition(47.0, 8.0);
        nav.Apply("navigation.courseOverGroundTrue", 0.0);
        nav.Apply("navigation.speedOverGround", 5.0);

        // First push: throws inside Update. The outer catch swallows.
        await svc.PushAsync(nav);
        await Assert.That(js.SuccessfulPushes).IsEqualTo(0);

        // Second push: identical inputs. If the cache had committed,
        // the skip-optimisation would short-circuit to 0 calls. With
        // the deferred commit, the real push runs.
        await svc.PushAsync(nav);
        await Assert.That(js.SuccessfulPushes).IsEqualTo(1)
            .Because("a failed push must NOT poison the skip cache");
        await Assert.That(js.Updates[0].IsFull).IsTrue()
            .Because("after a failure the renderer's state is unknown");
    }

    [Test]
    public async Task PushAsync_NonJsException_IsCaught_DoesNotPropagate()
    {
        // Outer catch was previously narrowed to JSException; an
        // InvalidOperationException from a stale Leaflet handle (the
        // radar-HUD regression) propagated past, tripped Blazor's
        // renderer error UI, and emptied the chart. Pin that the
        // catch is broad enough to keep the page functional.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0);
        var js = new ThrowAlwaysNonJsAisJs();
        var svc = NewService(store, js);

        // Must not throw out. The handled-and-recovered diagnostic
        // goes to Console.WriteLine; this assertion just pins the
        // no-throw contract.
        await svc.PushAsync(new NavigationData());
        await Assert.That(js.AttemptedPushes).IsEqualTo(1);
    }

    // --- Threat-band integration pins (m7 from the PR-265 follow-up) -
    // The two-tier classification is unit-tested at the helper
    // level (CpaTests.ClassifyThreat_*); these tests pin that the
    // plumbing through BuildSnapshot ends up writing the right
    // wire-string into the JS-bound payload. Cheap insurance against
    // a future plumbing regression that swaps Cpa.ClassifyThreat for
    // an inline implementation.

    [Test]
    public async Task BuildSnapshot_AlarmTier_GeometryProducesAlarmWireString()
    {
        // Target ~0.3 nm north of own boat, head-on closing at 5 m/s
        // each. Projected CPA ~ 0 nm; with default CpaAlarmNm = 0.1
        // and TcpaAlarmMin = 30, classification lands in the alarm
        // tier.
        const double ownLat = 47.0, ownLon = 8.0;
        // 0.3 nm = 555.6 m. dLat = 555.6 / 111320 = 0.00499 deg.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111",
            ownLat + 555.6 / 111320.0, ownLon, sog: 5.0, cog: Math.PI);

        var nav = new NavigationData();
        nav.ApplyPosition(ownLat, ownLon);
        nav.Apply("navigation.courseOverGroundTrue", 0.0);   // due north
        nav.Apply("navigation.speedOverGround", 5.0);
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(nav);

        await Assert.That(js.V(0)[0].CpaThreat).IsEqualTo("alarm");
    }

    [Test]
    public async Task BuildSnapshot_UsesTrueOwnCourse_WhenHelmPrefersMagnetic()
    {
        // AIS COG is true; own COG must be true too. A 20 deg magnetic
        // offset on a 1.5 km head-on closer would push projected CPA to
        // ~0.14 nm (outside the 0.1 nm alarm tier).
        const double ownLat = 47.0, ownLon = 8.0;
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111",
            ownLat + 1500 / 111320.0, ownLon, sog: 5.0, cog: Math.PI);
        var nav = new NavigationData { PreferMagneticCourse = true };
        nav.ApplyPosition(ownLat, ownLon);
        nav.Apply("navigation.courseOverGroundTrue", 0.0);
        nav.Apply("navigation.courseOverGroundMagnetic", 20 * Math.PI / 180);
        nav.Apply("navigation.speedOverGround", 5.0);
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(nav);

        await Assert.That(js.V(0)[0].CpaThreat).IsEqualTo("alarm");
    }

    [Test]
    public async Task BuildSnapshot_FixTooOldToProject_GetsNoThreat()
    {
        // Same alarm-tier geometry, but the target's last fix is older
        // than AisDeadReckoning.MaxProjectionAge: the chip must agree
        // with CpaAlarmRule and drop the CPA rather than draw one from
        // a position the target left minutes ago.
        const double ownLat = 47.0, ownLon = 8.0;
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111",
            ownLat + 555.6 / 111320.0, ownLon, sog: 5.0, cog: Math.PI);
        store.GetVessels()[0].PositionUpdatedUtc =
            now - OnaPlotter.Utilities.AisDeadReckoning.MaxProjectionAge - TimeSpan.FromSeconds(1);
        var nav = new NavigationData();
        nav.ApplyPosition(ownLat, ownLon);
        nav.Apply("navigation.courseOverGroundTrue", 0.0);
        nav.Apply("navigation.speedOverGround", 5.0);
        var js = new FakeAisJs();
        var svc = NewService(store, js, time: new FakeTime(now));

        await svc.PushAsync(nav);

        await Assert.That(js.V(0)[0].CpaThreat).IsEqualTo("none");
    }

    [Test]
    public async Task BuildSnapshot_AwarenessTier_GeometryProducesAwarenessWireString()
    {
        // Crossing target with a deliberate lateral offset so the
        // projected CPA lands between the alarm tier (0.1 nm default)
        // and the awareness tier (1.0 nm default). Geometry: target
        // 0.5 nm north and 0.5 nm east of own, heading south at the
        // same speed. As own goes north and target goes south, they
        // cross paths with ~0.5 nm lateral separation -> CPA ~0.5 nm
        // -> Awareness band.
        const double ownLat = 47.0, ownLon = 8.0;
        double metresPerDegLon = 111_320.0 * Math.Cos(ownLat * Math.PI / 180.0);
        double dLat = (0.5 * 1852.0) / 111_320.0;
        double dLon = (0.5 * 1852.0) / metresPerDegLon;

        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111",
            ownLat + dLat, ownLon + dLon, sog: 5.0, cog: Math.PI);

        var nav = new NavigationData();
        nav.ApplyPosition(ownLat, ownLon);
        nav.Apply("navigation.courseOverGroundTrue", 0.0);
        nav.Apply("navigation.speedOverGround", 5.0);
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(nav);

        await Assert.That(js.V(0)[0].CpaThreat).IsEqualTo("awareness");
    }

    [Test]
    public async Task BuildSnapshot_NonClosingVessel_ProducesNoneWireString()
    {
        // Parallel-course vessel at the same speed never closes.
        // Cpa.Compute returns null for parallel-same-velocity pairs,
        // which surfaces as Threat.None on the wire. Pin so the
        // chart stays quiet on convoy formations.
        const double ownLat = 47.0, ownLon = 8.0;
        var store = new AisStore();
        // Target 1 nm east, going due north at the same speed as own
        // -> they cruise abreast and never approach each other.
        double metresPerDegLon = 111_320.0 * Math.Cos(ownLat * Math.PI / 180.0);
        double dLon = (1.0 * 1852.0) / metresPerDegLon;
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111",
            ownLat, ownLon + dLon, sog: 5.0, cog: 0.0);

        var nav = new NavigationData();
        nav.ApplyPosition(ownLat, ownLon);
        nav.Apply("navigation.courseOverGroundTrue", 0.0);
        nav.Apply("navigation.speedOverGround", 5.0);
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(nav);

        await Assert.That(js.V(0)[0].CpaThreat).IsEqualTo("none");
    }

    [Test]
    public async Task BuildSnapshot_BuddyVessel_ProducesNoneWireString_EvenInAlarmTier()
    {
        // Same alarm-tier geometry as the band test above, but the
        // vessel is on the buddy list. ClassifyThreat short-circuits
        // to None when isBuddy=true, regardless of distance.
        const double ownLat = 47.0, ownLon = 8.0;
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111",
            ownLat + 555.6 / 111320.0, ownLon, sog: 5.0, cog: Math.PI);
        store.UpdateBuddies(new[] { "vessels.urn:mrn:imo:mmsi:111" });

        var nav = new NavigationData();
        nav.ApplyPosition(ownLat, ownLon);
        nav.Apply("navigation.courseOverGroundTrue", 0.0);
        nav.Apply("navigation.speedOverGround", 5.0);
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(nav);

        await Assert.That(js.V(0)[0].CpaThreat).IsEqualTo("none");
    }

    [Test]
    public async Task BuildSnapshot_NonThreatVessel_HasNullColregsLabels()
    {
        // PR-265 made COLREGS lazy: only computed when threat is
        // Awareness or Alarm. Pin that the lazy path means popups
        // for non-threats see null labels (the JS popup then
        // suppresses the COLREGS row entirely - correct, since the
        // rules apply to closing encounters). Use a parallel-course
        // vessel so the threat is provably None.
        const double ownLat = 47.0, ownLon = 8.0;
        var store = new AisStore();
        double metresPerDegLon = 111_320.0 * Math.Cos(ownLat * Math.PI / 180.0);
        double dLon = (1.0 * 1852.0) / metresPerDegLon;
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111",
            ownLat, ownLon + dLon, sog: 5.0, cog: 0.0);

        var nav = new NavigationData();
        nav.ApplyPosition(ownLat, ownLon);
        nav.Apply("navigation.courseOverGroundTrue", 0.0);
        nav.Apply("navigation.speedOverGround", 5.0);
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(nav);

        await Assert.That(js.V(0)[0].CpaThreat).IsEqualTo("none");
        await Assert.That(js.V(0)[0].ColregsLabel).IsNull();
        await Assert.That(js.V(0)[0].ColregsRole).IsNull();
    }

    [Test]
    public async Task BuildSnapshot_ThreatVessel_PopulatesColregsLabels()
    {
        // The complementary pin: a vessel in the alarm tier gets
        // COLREGS labels populated. This exercises the lazy-COLREGS
        // path AND the head-on geometry classifier.
        const double ownLat = 47.0, ownLon = 8.0;
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111",
            ownLat + 555.6 / 111320.0, ownLon, sog: 5.0, cog: Math.PI);

        var nav = new NavigationData();
        nav.ApplyPosition(ownLat, ownLon);
        nav.Apply("navigation.courseOverGroundTrue", 0.0);
        nav.Apply("navigation.speedOverGround", 5.0);
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(nav);

        await Assert.That(js.V(0)[0].CpaThreat).IsEqualTo("alarm");
        await Assert.That(js.V(0)[0].ColregsLabel).IsNotNull();
        // Role can legitimately be null for indeterminate categories;
        // we just pin that the label landed (the head-on case maps
        // to "Head-on" - both vessels give way).
    }

    [Test]
    public async Task ThreatToWireString_ContractIsStable()
    {
        // Pin the C#/JS contract: aisLayer.js reads
        //   case 'alarm': ... case 'awareness': ... default: none
        // so any rename here silently breaks the chart-overlay
        // classifier on the JS side. Test reads the static helper
        // (internal-visible to tests via InternalsVisibleTo) so a
        // rename of the underlying enum case still surfaces here at
        // compile time.
        await Assert.That(AisPushService.ThreatToWireString(
            OnaPlotter.Utilities.Cpa.Threat.Alarm)).IsEqualTo("alarm");
        await Assert.That(AisPushService.ThreatToWireString(
            OnaPlotter.Utilities.Cpa.Threat.Awareness)).IsEqualTo("awareness");
        await Assert.That(AisPushService.ThreatToWireString(
            OnaPlotter.Utilities.Cpa.Threat.None)).IsEqualTo("none");
    }

    private sealed class ThrowOnceAisJs : IMapAisJs
    {
        public int AttemptedPushes;
        public int SuccessfulPushes;

        public List<AisUpdatePayload> Updates { get; } = [];

        public Task<bool> UpdateAisTargetsAsync(AisUpdatePayload update)
        {
            AttemptedPushes++;
            if (AttemptedPushes == 1)
                throw new Microsoft.JSInterop.JSException("simulated transient JS-side failure");
            SuccessfulPushes++;
            Updates.Add(update);
            return Task.FromResult(true);
        }

        public Task SetAtonsAsync(AtonPayload[] atons) => Task.CompletedTask;
        public Task SetAtonsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetAisLabelsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetOwnMmsiAsync(string mmsi) => Task.CompletedTask;
        public Task SetOwnCallsignAsync(string callsign) => Task.CompletedTask;
        public Task SetHarborModeAsync(bool enabled) => Task.CompletedTask;
        public Task<bool> FocusVesselAsync(string context) => Task.FromResult(false);
    }

    private sealed class ThrowAlwaysNonJsAisJs : IMapAisJs
    {
        public int AttemptedPushes;

        public Task<bool> UpdateAisTargetsAsync(AisUpdatePayload update)
        {
            AttemptedPushes++;
            // InvalidOperationException is the canonical "Leaflet
            // handle stale" shape on the radar-HUD path the helm
            // hit. Throwing it through here pins that the outer
            // catch is broad enough to not let it escape.
            throw new InvalidOperationException("simulated stale Leaflet handle");
        }

        public Task SetAtonsAsync(AtonPayload[] atons) => Task.CompletedTask;
        public Task SetAtonsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetAisLabelsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetOwnMmsiAsync(string mmsi) => Task.CompletedTask;
        public Task SetOwnCallsignAsync(string callsign) => Task.CompletedTask;
        public Task SetHarborModeAsync(bool enabled) => Task.CompletedTask;
        public Task<bool> FocusVesselAsync(string context) => Task.FromResult(false);
    }

    [Test]
    public async Task Payload_Forwards_AisVessel_Loa_And_Beam_Values()
    {
        // Two vessels: one with both dimensions set via the canonical
        // SK shapes, one with neither. Pin (a) values flow through to
        // the JS payload verbatim, and (b) absence stays null (the JS
        // popup's "no dimensions" path branches on null/non-null -
        // a defaulted-to-0 leak here would render "0m / 0m").
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0);
        store.Apply("vessels.urn:mrn:imo:mmsi:111", "design.length",
            JsonSerializer.SerializeToElement(new { overall = 32.5 }));
        store.Apply("vessels.urn:mrn:imo:mmsi:111", "design.beam",
            JsonSerializer.SerializeToElement(6.2));
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:222", 47.1, 8.1);

        var js = new FakeAisJs();
        var svc = NewService(store, js);
        await svc.PushAsync(new NavigationData());

        // Payload order is store-determined; locate by context.
        var entries = js.V(0);
        var dims = entries.First(e => e.Context == "vessels.urn:mrn:imo:mmsi:111");
        var bare = entries.First(e => e.Context == "vessels.urn:mrn:imo:mmsi:222");

        await Assert.That(dims.Static!.Subtitle).IsEqualTo("MMSI 111 · 32.5 × 6.2 m");
        await Assert.That(bare.Static!.Subtitle).IsEqualTo("MMSI 222");
    }

    [Test]
    public async Task Opacity_FollowsStalenessRamp_AgainstInactiveSetting()
    {
        // 120 s since the last AIS evidence. Default 5 min inactive
        // threshold puts it on the ramp; a 1 min threshold pins it at
        // the floor.
        // AisStore prunes against the wall clock, so stay near it.
        var now = DateTime.UtcNow;
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111", "navigation.position", pos, now.AddSeconds(-120));

        var settings = new FakeSettings { AisInactiveMinutes = 5 };
        var js = new FakeAisJs();
        var svc = NewService(store, js, settings, time: new FakeTime(now));
        await svc.PushAsync(new NavigationData());
        await Assert.That(js.V(0)[0].Opacity).IsEqualTo("0.78");

        settings.AisInactiveMinutes = 1;
        var js2 = new FakeAisJs();
        await NewService(store, js2, settings, time: new FakeTime(now)).PushAsync(new NavigationData());
        await Assert.That(js2.V(0)[0].Opacity).IsEqualTo("0.25");
    }

    [Test]
    public async Task Opacity_NullForFreshTargets()
    {
        // AisStore prunes against the wall clock, so stay near it.
        var now = DateTime.UtcNow;
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111", "navigation.position", pos, now.AddSeconds(-5));
        var js = new FakeAisJs();
        await NewService(store, js, time: new FakeTime(now)).PushAsync(new NavigationData());
        await Assert.That(js.V(0)[0].Opacity).IsNull();
    }

    // --- Wire protocol: full snapshot, then versioned deltas ----------

    private static NavigationData OwnAt(double lat, double lon, double cog = 0.0, double sog = 5.0)
    {
        var nav = new NavigationData();
        nav.ApplyPosition(lat, lon);
        nav.Apply("navigation.courseOverGroundTrue", cog);
        nav.Apply("navigation.speedOverGround", sog);
        return nav;
    }

    [Test]
    public async Task FirstPush_IsFullSnapshot_WithStaticAndTrail()
    {
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, name: "A");
        var js = new FakeAisJs();
        await NewService(store, js).PushAsync(new NavigationData());

        var u = js.Updates[0];
        await Assert.That(u.IsFull).IsTrue();
        await Assert.That(u.Seq).IsEqualTo(1);
        await Assert.That(u.V0().Static).IsNotNull();
        await Assert.That(u.V0().Trail).IsEquivalentTo(new[] { 47.0, 8.0 });
        await Assert.That(u.V0().TrailAppend).IsNull();
    }

    [Test]
    public async Task UnchangedTick_SendsNothing_EvenAfterSkipWindow()
    {
        // Nothing moved: the skip gate elides the build inside the
        // window, and past it the diff is empty, so no interop call.
        var time = new FakeTime(DateTime.UtcNow);
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, name: "A");
        var js = new FakeAisJs();
        var svc = NewService(store, js, time: time);

        await svc.PushAsync(new NavigationData());
        await svc.PushAsync(new NavigationData());
        time.Advance(AisPushService.MaxSkipInterval + TimeSpan.FromSeconds(1));
        await svc.PushAsync(new NavigationData());

        await Assert.That(js.Updates.Count).IsEqualTo(1);
    }

    [Test]
    public async Task MovedVessel_SentAsDelta_WithoutStatic_WithTrailAppend()
    {
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, name: "A");
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:222", 47.5, 8.5, name: "B");
        var js = new FakeAisJs();
        var svc = NewService(store, js);
        await svc.PushAsync(new NavigationData());

        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.001, 8.0);
        await svc.PushAsync(new NavigationData());

        var u = js.Updates[1];
        await Assert.That(u.IsFull).IsFalse();
        await Assert.That(u.BaseSeq).IsEqualTo(js.Updates[0].Seq);
        await Assert.That(u.Vessels.Length).IsEqualTo(1);
        var v = u.V0();
        await Assert.That(v.Context).IsEqualTo("vessels.urn:mrn:imo:mmsi:111");
        await Assert.That(v.Lat).IsEqualTo(47.001);
        await Assert.That(v.Static).IsNull()
            .Because("identity fields are unchanged");
        await Assert.That(v.Trail).IsNull();
        await Assert.That(v.TrailAppend).IsEquivalentTo(new[] { 47.001, 8.0 })
            .Because("only the new trail point goes on the wire");
    }

    [Test]
    public async Task RenamedVessel_ResendsStatic()
    {
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, name: "Old");
        var js = new FakeAisJs();
        var svc = NewService(store, js);
        await svc.PushAsync(new NavigationData());

        store.Apply("vessels.urn:mrn:imo:mmsi:111", "name", "New");
        await svc.PushAsync(new NavigationData());

        await Assert.That(js.Updates[1].V0().Static!.Title).IsEqualTo("New");
    }

    [Test]
    public async Task RefusedDelta_IsAnsweredWithFullSnapshot()
    {
        // The renderer lost the base of the delta (map re-init): it
        // refuses and the service resends everything in the same tick.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, name: "A");
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:222", 47.5, 8.5, name: "B");
        var js = new FakeAisJs();
        var svc = NewService(store, js);
        await svc.PushAsync(new NavigationData());

        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.001, 8.0);
        js.Answers.Enqueue(false);
        await svc.PushAsync(new NavigationData());

        await Assert.That(js.Updates.Count).IsEqualTo(3);
        await Assert.That(js.Updates[1].IsFull).IsFalse();
        await Assert.That(js.Updates[2].IsFull).IsTrue();
        await Assert.That(js.Updates[2].Vessels.Length).IsEqualTo(2);
        await Assert.That(js.Updates[2].Vessels.All(v => v.Static is not null && v.Trail is not null)).IsTrue();
    }

    [Test]
    public async Task RefusedFullSnapshot_RetriesFullNextTick()
    {
        // A renderer deferring mid-drag on a slow client refuses even
        // the full snapshot; the next tick must not fall back to a delta.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0, name: "A");
        var js = new FakeAisJs();
        var svc = NewService(store, js);
        js.Answers.Enqueue(false);
        await svc.PushAsync(new NavigationData());
        await svc.PushAsync(new NavigationData());

        await Assert.That(js.Updates.Count).IsEqualTo(2);
        await Assert.That(js.Updates[1].IsFull).IsTrue();
    }

    [Test]
    public async Task AlarmTarget_ResolvesOverlayTonePulseAndPopupFields()
    {
        // Head-on closer ~0.3 nm ahead: alarm tier.
        const double ownLat = 47.0, ownLon = 8.0;
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111",
            ownLat + 555.6 / 111320.0, ownLon, sog: 5.0, cog: Math.PI, name: "Closer");
        var js = new FakeAisJs();
        await NewService(store, js).PushAsync(OwnAt(ownLat, ownLon));

        var v = js.V(0)[0];
        await Assert.That(v.CpaOverlay).IsEqualTo(AisCpaOverlay.Alarm);
        await Assert.That(v.MarkerTone).IsEqualTo(AisTone.Danger);
        await Assert.That(v.VectorTone).IsEqualTo(AisTone.Ship)
            .Because("the COG vector keeps the identity colour in alarm");
        await Assert.That(v.Pulse).IsTrue();
        await Assert.That(v.Snoozable).IsTrue();
        await Assert.That(v.CpaPointLat).IsNotNull();
        await Assert.That(v.OwnCpaPointLat).IsNotNull();
        await Assert.That(v.CpaNm).IsNotNull();
        await Assert.That(v.TcpaMin!.Value).IsGreaterThanOrEqualTo(0);
        await Assert.That(v.ColregsGiveWay).IsTrue()
            .Because("head-on: both vessels give way");
        await Assert.That(v.RangeNm!.Value).IsEqualTo(0.3).Within(0.01);
        await Assert.That(v.BearingDeg).IsEqualTo(0.0);
        await Assert.That(v.SogKn).IsEqualTo(9.7);
        await Assert.That(v.CogDeg).IsEqualTo(180.0);
    }

    [Test]
    public async Task AwarenessTarget_OverlayIsSelectionGated_NoPulse()
    {
        const double ownLat = 47.0, ownLon = 8.0;
        double metresPerDegLon = 111_320.0 * Math.Cos(ownLat * Math.PI / 180.0);
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111",
            ownLat + 926.0 / 111_320.0, ownLon + 926.0 / metresPerDegLon, sog: 5.0, cog: Math.PI);
        var js = new FakeAisJs();
        await NewService(store, js).PushAsync(OwnAt(ownLat, ownLon));

        var v = js.V(0)[0];
        await Assert.That(v.CpaOverlay).IsEqualTo(AisCpaOverlay.Awareness);
        await Assert.That(v.Pulse).IsFalse();
        await Assert.That(v.MarkerTone).IsEqualTo(AisTone.Ship);
        await Assert.That(v.Snoozable).IsTrue();
    }

    [Test]
    public async Task NoOwnFix_NoRangeBearing_NoCpa()
    {
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0);
        var js = new FakeAisJs();
        await NewService(store, js).PushAsync(new NavigationData());

        var v = js.V(0)[0];
        await Assert.That(v.RangeNm).IsNull();
        await Assert.That(v.BearingDeg).IsNull();
        await Assert.That(v.CpaOverlay).IsEqualTo(AisCpaOverlay.None);
        await Assert.That(v.CpaNm).IsNull();
        await Assert.That(v.Snoozable).IsFalse();
    }

    [Test]
    public async Task StaleTarget_HidesLabel_ButBuddyKeepsIt()
    {
        var now = DateTime.UtcNow;
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111", "navigation.position", pos, now.AddMinutes(-3));
        store.Apply("vessels.urn:mrn:imo:mmsi:222", "navigation.position", pos, now.AddMinutes(-3));
        store.UpdateBuddies(new[] { "vessels.urn:mrn:imo:mmsi:222" });
        var settings = new FakeSettings { AisInactiveMinutes = 2 };
        var js = new FakeAisJs();
        await NewService(store, js, settings, time: new FakeTime(now)).PushAsync(new NavigationData());

        var ghost = js.V(0).First(v => v.Context.EndsWith("111"));
        var buddy = js.V(0).First(v => v.Context.EndsWith("222"));
        await Assert.That(ghost.LabelVisible).IsFalse();
        await Assert.That(ghost.Opacity).IsEqualTo("0.25");
        await Assert.That(buddy.LabelVisible).IsTrue();
        await Assert.That(buddy.Opacity).IsNull();
    }

    [Test]
    public async Task InactiveSettingChange_PushesWithinSkipWindow()
    {
        // The skip gate keys on the AIS-inactive setting so a Settings
        // change reaches the chart on the next tick.
        var now = DateTime.UtcNow;
        var store = new AisStore();
        var pos = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        store.Apply("vessels.urn:mrn:imo:mmsi:111", "navigation.position", pos, now.AddMinutes(-3));
        var settings = new FakeSettings { AisInactiveMinutes = 10 };
        var js = new FakeAisJs();
        var svc = NewService(store, js, settings, time: new FakeTime(now));
        await svc.PushAsync(new NavigationData());

        settings.AisInactiveMinutes = 2;
        await svc.PushAsync(new NavigationData());

        await Assert.That(js.Updates.Count).IsEqualTo(2);
        await Assert.That(js.V(1)[0].LabelVisible).IsFalse();
    }

    [Test]
    public async Task TickWhilePushInFlight_IsSkipped()
    {
        // A slow renderer still holding the previous push: the next
        // timer tick must not build a second delta on the same base.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0);
        var js = new GatedAisJs();
        var svc = NewService(store, js);

        var first = svc.PushAsync(new NavigationData());
        await svc.PushAsync(OwnAt(47.0, 8.0));
        js.Gate.SetResult(true);
        await first;

        await Assert.That(js.Calls).IsEqualTo(1);
    }
}

internal sealed class GatedAisJs : IMapAisJs
{
    public TaskCompletionSource<bool> Gate { get; } = new();
    public int Calls;
    public Task<bool> UpdateAisTargetsAsync(AisUpdatePayload update) { Calls++; return Gate.Task; }
    public Task SetAtonsAsync(AtonPayload[] atons) => Task.CompletedTask;
    public Task SetAtonsVisibleAsync(bool visible) => Task.CompletedTask;
    public Task SetAisLabelsVisibleAsync(bool visible) => Task.CompletedTask;
    public Task SetOwnMmsiAsync(string mmsi) => Task.CompletedTask;
    public Task SetOwnCallsignAsync(string callsign) => Task.CompletedTask;
    public Task SetHarborModeAsync(bool enabled) => Task.CompletedTask;
    public Task<bool> FocusVesselAsync(string context) => Task.FromResult(false);
}

internal static class AisUpdatePayloadTestExtensions
{
    public static AisVesselPayload V0(this AisUpdatePayload u) => u.Vessels[0];
}
