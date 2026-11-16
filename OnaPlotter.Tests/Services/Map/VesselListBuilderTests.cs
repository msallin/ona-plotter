using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Map;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the Layers-panel vessel list: dead-reckoned CPA (agreeing with
/// the alarm rule), sort order, and the quantised cache key that keeps
/// own-ship sensor noise from rebuilding the list every render.
/// </summary>
public class VesselListBuilderTests
{
    private const double OwnLat = 47.0, OwnLon = 8.0;

    private static NavigationData Own(double lat = OwnLat, double lon = OwnLon, double cog = 0.0, double sog = 5.0)
    {
        var nav = new NavigationData();
        nav.ApplyPosition(lat, lon);
        nav.Apply("navigation.courseOverGroundTrue", cog);
        nav.Apply("navigation.speedOverGround", sog);
        return nav;
    }

    private static AisVessel Vessel(string ctx, double lat, double lon, double? sog, double? cog,
        DateTime? fixUtc = null)
    {
        var store = new AisStore();
        store.Apply(ctx, "navigation.position",
            JsonSerializer.SerializeToElement(new { latitude = lat, longitude = lon }));
        if (sog is double s) store.Apply(ctx, "navigation.speedOverGround", s);
        if (cog is double c) store.Apply(ctx, "navigation.courseOverGroundTrue", c);
        var v = store.GetVessels()[0];
        if (fixUtc is DateTime f) v.PositionUpdatedUtc = f;
        return v;
    }

    [Test]
    public async Task Build_ClosingTarget_GetsCpaAndColregs_SortedFirst()
    {
        var now = DateTime.UtcNow;
        // Head-on closer 0.3 nm ahead; a far, parallel target 1 nm east.
        double dLon = 1852.0 / (111_320.0 * Math.Cos(OwnLat * Math.PI / 180.0));
        var far = Vessel("vessels.urn:mrn:imo:mmsi:222", OwnLat, OwnLon + dLon, 5.0, 0.0, now);
        var closer = Vessel("vessels.urn:mrn:imo:mmsi:111", OwnLat + 555.6 / 111_320.0, OwnLon, 5.0, Math.PI, now);

        var list = VesselListBuilder.Build([far, closer], Own(), new FakeSettings(), now);

        await Assert.That(list[0].Context).IsEqualTo(closer.Context);
        await Assert.That(list[0].CpaNm).IsNotNull();
        await Assert.That(list[0].ColregsLabel).IsNotNull();
        await Assert.That(list[0].DistanceNm!.Value).IsEqualTo(0.3).Within(0.01);
        await Assert.That(list[1].TcpaMin).IsNull();
    }

    [Test]
    public async Task Build_UsesDeadReckonedPosition_LikeTheAlarmRule()
    {
        // Fix 60 s old, target heading north at 10 m/s from 600 m due
        // east: dead-reckoned it is 600 m further north, which changes
        // the CPA versus the stale fix.
        var now = DateTime.UtcNow;
        double dLon = 600.0 / (111_320.0 * Math.Cos(OwnLat * Math.PI / 180.0));
        var v = Vessel("vessels.urn:mrn:imo:mmsi:111", OwnLat, OwnLon + dLon, 10.0, 0.0, now.AddSeconds(-60));
        var own = Own(sog: 0.5);

        var list = VesselListBuilder.Build([v], own, new FakeSettings(), now);

        var snap = Cpa.PrecomputeOwn(OwnLat, OwnLon, 0.0, 0.5)!.Value;
        var dr = AisDeadReckoning.PositionAt(v, now)!.Value;
        var expected = Cpa.Compute(snap, dr.Latitude, dr.Longitude, 0.0, 10.0);
        var stale = Cpa.Compute(snap, OwnLat, OwnLon + dLon, 0.0, 10.0);
        await Assert.That(list[0].CpaNm).IsEqualTo(expected?.CpaNm);
        await Assert.That(list[0].TcpaMin).IsNotEqualTo(stale?.TcpaMin);
    }

    [Test]
    public async Task Build_FixTooOldToProject_HasNoCpa()
    {
        var now = DateTime.UtcNow;
        var v = Vessel("vessels.urn:mrn:imo:mmsi:111", OwnLat + 555.6 / 111_320.0, OwnLon, 5.0, Math.PI,
            now - AisDeadReckoning.MaxProjectionAge - TimeSpan.FromSeconds(1));

        var list = VesselListBuilder.Build([v], Own(), new FakeSettings(), now);

        await Assert.That(list[0].CpaNm).IsNull();
        await Assert.That(list[0].DistanceNm).IsNotNull();
    }

    [Test]
    public async Task Build_NoOwnFix_ListsTargetsWithoutRelativeFigures()
    {
        var now = DateTime.UtcNow;
        var v = Vessel("vessels.urn:mrn:imo:mmsi:111", OwnLat, OwnLon, 5.0, 0.0, now);

        var list = VesselListBuilder.Build([v], new NavigationData(), new FakeSettings(), now);

        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list[0].DistanceNm).IsNull();
        await Assert.That(list[0].CpaNm).IsNull();
        await Assert.That(list[0].DisplayName).IsEqualTo("MMSI 111");
    }

    // - Cache key ---------------------------------------------------

    [Test]
    public async Task Key_OwnJitterBelowQuantum_IsStable()
    {
        // ~5 m position noise and 0.2 deg COG wobble at rest.
        var now = new DateTime(2026, 1, 1, 12, 0, 1, DateTimeKind.Utc);
        var s = new FakeSettings();
        var a = VesselListBuilder.KeyFor(1, Own(47.00001, 8.00001, 0.100), s, now);
        var b = VesselListBuilder.KeyFor(1, Own(47.00005, 8.00004, 0.103), s, now);
        await Assert.That(a).IsEqualTo(b);
    }

    [Test]
    public async Task Key_OwnMovedPastQuantum_Changes()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 1, DateTimeKind.Utc);
        var s = new FakeSettings();
        var a = VesselListBuilder.KeyFor(1, Own(47.0), s, now);
        var b = VesselListBuilder.KeyFor(1, Own(47.001), s, now);   // ~110 m north
        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task Key_AisVersionOrTimeBucket_Changes()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 1, DateTimeKind.Utc);
        var s = new FakeSettings();
        var a = VesselListBuilder.KeyFor(1, Own(), s, now);
        await Assert.That(VesselListBuilder.KeyFor(2, Own(), s, now)).IsNotEqualTo(a);
        await Assert.That(VesselListBuilder.KeyFor(1, Own(), s, now + VesselListBuilder.TimeBucket)).IsNotEqualTo(a);
        await Assert.That(VesselListBuilder.KeyFor(1, Own(), s, now.AddMilliseconds(500))).IsEqualTo(a);
    }

    [Test]
    public async Task Get_ReturnsSameInstanceWhileKeyHolds()
    {
        var now = DateTime.UtcNow;
        var store = new AisStore();
        store.Apply("vessels.urn:mrn:imo:mmsi:111", "navigation.position",
            JsonSerializer.SerializeToElement(new { latitude = 47.01, longitude = 8.0 }));
        var builder = new VesselListBuilder();
        var settings = new FakeSettings();

        var first = builder.Get(store, Own(), settings, now);
        var second = builder.Get(store, Own(47.00001), settings, now);
        var moved = builder.Get(store, Own(47.01), settings, now);

        await Assert.That(second).IsSameReferenceAs(first);
        await Assert.That(moved).IsNotSameReferenceAs(first);
    }
}
