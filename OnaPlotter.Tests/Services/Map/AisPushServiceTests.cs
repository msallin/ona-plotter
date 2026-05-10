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
        public List<OnaPlotter.Services.Map.AisVesselPayload[]> Pushes { get; } = [];

        public Task UpdateAisTargetsAsync(OnaPlotter.Services.Map.AisVesselPayload[] vessels)
        {
            // Snapshot the array refs at push time. Element instances
            // are pool-shared - subsequent pushes mutate the same
            // payload objects in place. Tests in this file inspect
            // Pushes[N] immediately after the Nth PushAsync, before
            // the next push runs, so the pool aliasing is invisible.
            // If a future test does multiple pushes and reads earlier
            // entries afterward, it MUST clone the field values into
            // locals before the next push fires.
            Pushes.Add(vessels);
            return Task.CompletedTask;
        }

        public Task SetAtonsAsync(object[] atons) => Task.CompletedTask;
        public Task SetAtonsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetAisLabelsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetOwnMmsiAsync(string mmsi) => Task.CompletedTask;
        public Task SetOwnCallsignAsync(string callsign) => Task.CompletedTask;
        public Task SetHarborModeAsync(bool enabled) => Task.CompletedTask;
        public Task<bool> FocusVesselAsync(string context) => Task.FromResult(false);
        public Task SetGuardZoneVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetGuardZoneWarningRingVisibleAsync(bool visible) => Task.CompletedTask;
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

        await Assert.That(js.Pushes.Count).IsEqualTo(1);
        await Assert.That(js.Pushes[0].Length).IsEqualTo(0);
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

        await Assert.That(js.Pushes[0].Length).IsEqualTo(1);
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

        // The moored vessel drops on the second push; the active one stays.
        await Assert.That(js.Pushes[1].Length).IsEqualTo(1);
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
        var entry = js.Pushes[0][0];
        await Assert.That(entry.Context).IsEqualTo("vessels.urn:mrn:imo:mmsi:111");
        await Assert.That(entry.Lat).IsEqualTo(47.0);
        await Assert.That(entry.Lon).IsEqualTo(8.0);
        await Assert.That(entry.DisplayName).IsEqualTo("Test Boat");
        await Assert.That(entry.Source).IsEqualTo("ais");
        await Assert.That(entry.CpaThreat).IsEqualTo("none");
        // AgeSec is computed from (testTime - vessel.LastSeen). The
        // FakeTime is fixed at 2026-01-01 while AisStore.Apply stamps
        // LastSeen via DateTime.UtcNow on the real clock, so the sign
        // is environment-dependent. Just probe the property exists -
        // the field-presence contract is what this test enforces.
        _ = entry.AgeSec;
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
            CpaAlarmThreshold = 0.5,
            GuardZoneLookaheadMinutes = 30.0,
        };
        var svc = NewService(store, js, settings);

        await svc.PushAsync(nav);

        var entry = js.Pushes[0][0];
        // 0.09 nm CPA is OUTSIDE the anchor-narrowed warning band
        // (~0.032 nm) - the chip should be classified None. Without
        // the AisPushService fix this returns "warning" because the
        // 0.5 nm underway threshold's warning band reaches to 1.0 nm.
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

        var entry = js.Pushes[0][0];
        await Assert.That(entry.DisplayName).IsEqualTo("★ Friend");
    }

    // --- LOA / Beam payload shape (PR #221) ---------------------------
    //
    // The popup + VesselsSection both render a "32m / 6m" dimensions
    // row only when these fields are present on the JS-side payload.
    // A rename in the anonymous record (loaM -> lengthOverallM,
    // beamM -> beam, ...) would silently strip the row from the helm
    // UI without any compile-time signal - the JS reads via property
    // name, which crosses the C#/JS contract gap. These pins go red on
    // the C# side BEFORE the JS regression hits the helm.

    [Test]
    public async Task Payload_Carries_loaM_And_beamM_Fields()
    {
        // Field-presence check, mirroring Payload_Has_The_Expected_Shape:
        // a renamed field on the anonymous record fails this test rather
        // than silently dropping the dimensions row in production.
        var store = new AisStore();
        SeedVessel(store, "vessels.urn:mrn:imo:mmsi:111", 47.0, 8.0);
        var js = new FakeAisJs();
        var svc = NewService(store, js);

        await svc.PushAsync(new NavigationData());

        // Direct property access on the typed payload - if either
        // field is renamed the compile fails here, which is the
        // contract this test exists to enforce.
        var entry = js.Pushes[0][0];
        _ = entry.LoaM;
        _ = entry.BeamM;
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
        await Assert.That(js.Pushes[0].Length).IsEqualTo(1);
        await Assert.That(js.Pushes[0][0].Context).IsEqualTo("vessels.urn:mrn:imo:mmsi:111");
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

        await Assert.That(js.Pushes[0].Length).IsEqualTo(1);
        await Assert.That(js.Pushes[0][0].Context).IsEqualTo("vessels.urn:mrn:imo:mmsi:111");
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

    [Test]
    public async Task ThreatToWireString_ContractIsStable()
    {
        // Pin the C#/JS contract: aisLayer.js reads
        //   case 'danger': ... case 'warning': ... default: none
        // so any rename here silently breaks the chart-overlay
        // classifier on the JS side. Test reads the static helper
        // (internal-visible to tests via InternalsVisibleTo) so a
        // rename of the underlying enum case still surfaces here at
        // compile time.
        await Assert.That(AisPushService.ThreatToWireString(
            OnaPlotter.Utilities.Cpa.Threat.Danger)).IsEqualTo("danger");
        await Assert.That(AisPushService.ThreatToWireString(
            OnaPlotter.Utilities.Cpa.Threat.Warning)).IsEqualTo("warning");
        await Assert.That(AisPushService.ThreatToWireString(
            OnaPlotter.Utilities.Cpa.Threat.None)).IsEqualTo("none");
    }

    private sealed class ThrowOnceAisJs : IMapAisJs
    {
        public int AttemptedPushes;
        public int SuccessfulPushes;

        public Task UpdateAisTargetsAsync(AisVesselPayload[] vessels)
        {
            AttemptedPushes++;
            if (AttemptedPushes == 1)
                throw new Microsoft.JSInterop.JSException("simulated transient JS-side failure");
            SuccessfulPushes++;
            return Task.CompletedTask;
        }

        public Task SetAtonsAsync(object[] atons) => Task.CompletedTask;
        public Task SetAtonsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetAisLabelsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetOwnMmsiAsync(string mmsi) => Task.CompletedTask;
        public Task SetOwnCallsignAsync(string callsign) => Task.CompletedTask;
        public Task SetHarborModeAsync(bool enabled) => Task.CompletedTask;
        public Task<bool> FocusVesselAsync(string context) => Task.FromResult(false);
        public Task SetGuardZoneVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetGuardZoneWarningRingVisibleAsync(bool visible) => Task.CompletedTask;
    }

    private sealed class ThrowAlwaysNonJsAisJs : IMapAisJs
    {
        public int AttemptedPushes;

        public Task UpdateAisTargetsAsync(AisVesselPayload[] vessels)
        {
            AttemptedPushes++;
            // InvalidOperationException is the canonical "Leaflet
            // handle stale" shape on the radar-HUD path the helm
            // hit. Throwing it through here pins that the outer
            // catch is broad enough to not let it escape.
            throw new InvalidOperationException("simulated stale Leaflet handle");
        }

        public Task SetAtonsAsync(object[] atons) => Task.CompletedTask;
        public Task SetAtonsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetAisLabelsVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetOwnMmsiAsync(string mmsi) => Task.CompletedTask;
        public Task SetOwnCallsignAsync(string callsign) => Task.CompletedTask;
        public Task SetHarborModeAsync(bool enabled) => Task.CompletedTask;
        public Task<bool> FocusVesselAsync(string context) => Task.FromResult(false);
        public Task SetGuardZoneVisibleAsync(bool visible) => Task.CompletedTask;
        public Task SetGuardZoneWarningRingVisibleAsync(bool visible) => Task.CompletedTask;
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
        var entries = js.Pushes[0];
        var dims = entries.First(e => e.Context == "vessels.urn:mrn:imo:mmsi:111");
        var bare = entries.First(e => e.Context == "vessels.urn:mrn:imo:mmsi:222");

        await Assert.That(dims.LoaM).IsEqualTo(32.5);
        await Assert.That(dims.BeamM).IsEqualTo(6.2);

        await Assert.That(bare.LoaM).IsNull();
        await Assert.That(bare.BeamM).IsNull();
    }
}
