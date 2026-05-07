using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Tests;

public class AisVesselTests
{
    [Test]
    public async Task Constructor_SetsContext()
    {
        var vessel = new AisVessel("vessels.urn:mrn:imo:mmsi:211234567");
        await Assert.That(vessel.Context).IsEqualTo("vessels.urn:mrn:imo:mmsi:211234567");
    }

    [Test]
    public async Task Apply_Position_SetsLatLon()
    {
        var vessel = new AisVessel("vessels.test");
        var json = JsonSerializer.SerializeToElement(new { latitude = 47.39, longitude = 8.54 });
        await Assert.That(vessel.Apply("navigation.position", json)).IsTrue();
        await Assert.That(vessel.Latitude).IsEqualTo(47.39);
        await Assert.That(vessel.Longitude).IsEqualTo(8.54);
    }

    [Test]
    public async Task Apply_Position_MissingFields_ReturnsFalse()
    {
        var vessel = new AisVessel("vessels.test");
        var json = JsonSerializer.SerializeToElement(new { latitude = 47.39 });
        await Assert.That(vessel.Apply("navigation.position", json)).IsFalse();
    }

    [Test]
    public async Task Apply_SpeedOverGround_SetsProperty()
    {
        var vessel = new AisVessel("vessels.test");
        var je = JsonSerializer.SerializeToElement(5.0);
        await Assert.That(vessel.Apply("navigation.speedOverGround", je)).IsTrue();
        await Assert.That(vessel.SpeedOverGround).IsEqualTo(5.0);
    }

    [Test]
    public async Task Apply_CourseOverGround_SetsProperty()
    {
        var vessel = new AisVessel("vessels.test");
        var je = JsonSerializer.SerializeToElement(1.5);
        await Assert.That(vessel.Apply("navigation.courseOverGroundTrue", je)).IsTrue();
        await Assert.That(vessel.CourseOverGround).IsEqualTo(1.5);
    }

    [Test]
    public async Task Apply_HeadingTrue_SetsProperty()
    {
        var vessel = new AisVessel("vessels.test");
        var je = JsonSerializer.SerializeToElement(0.78);
        await Assert.That(vessel.Apply("navigation.headingTrue", je)).IsTrue();
        await Assert.That(vessel.Heading).IsEqualTo(0.78);
    }

    [Test]
    public async Task Apply_Name_SetsProperty()
    {
        var vessel = new AisVessel("vessels.test");
        var je = JsonSerializer.SerializeToElement("MV Tester");
        await Assert.That(vessel.Apply("name", je)).IsTrue();
        await Assert.That(vessel.Name).IsEqualTo("MV Tester");
    }

    [Test]
    public async Task Apply_Mmsi_SetsProperty()
    {
        var vessel = new AisVessel("vessels.test");
        var je = JsonSerializer.SerializeToElement("211234567");
        await Assert.That(vessel.Apply("mmsi", je)).IsTrue();
        await Assert.That(vessel.Mmsi).IsEqualTo("211234567");
    }

    [Test]
    public async Task Apply_Callsign_SetsProperty()
    {
        var vessel = new AisVessel("vessels.test");
        var je = JsonSerializer.SerializeToElement("HB9ABC");
        await Assert.That(vessel.Apply("communication.callsignVhf", je)).IsTrue();
        await Assert.That(vessel.Callsign).IsEqualTo("HB9ABC");
    }

    [Test]
    public async Task Apply_ShipType_WithNameProperty()
    {
        var vessel = new AisVessel("vessels.test");
        var je = JsonSerializer.SerializeToElement(new { name = "Cargo" });
        await Assert.That(vessel.Apply("design.aisShipType", je)).IsTrue();
        await Assert.That(vessel.ShipType).IsEqualTo("Cargo");
    }

    // --- design.length / design.beam shape coverage (PR #221) ---
    //
    // The wire-format readers below are load-bearing for the AIS popup +
    // VesselsSection LOA/Beam rendering. Three shapes flow into Apply
    // from the SK servers we run against:
    //   1. design.length as { overall, hull, waterline }  (canonical)
    //   2. design.length as a bare scalar number          (older feeds)
    //   3. design.length.overall as a leaf number         (SK leaf publish)
    // and design.beam as a bare scalar (no sub-keys per SK schema).
    // Each of these is pinned below so a future tweak to the case-block
    // ordering or the JsonElement-vs-double guards goes red here first
    // rather than silently dropping the dimensions row.

    [Test]
    public async Task Apply_DesignLength_ObjectWithOverall_SetsLoa()
    {
        // Canonical SK schema shape. Most signalk-ais-* plugins populate
        // just `overall` (dim A + dim B from AIS Type 5 / Type 24 static
        // messages). A regression that picked up `hull` instead would
        // silently misreport tugs / dredgers where overall != hull.
        var vessel = new AisVessel("vessels.test");
        var je = JsonSerializer.SerializeToElement(new { overall = 32.5 });
        await Assert.That(vessel.Apply("design.length", je)).IsTrue();
        await Assert.That(vessel.LengthOverallMeters).IsEqualTo(32.5);
    }

    [Test]
    public async Task Apply_DesignLength_BareScalar_SetsLoa()
    {
        // Some SK feeds (older signalk-n2k-ais, hand-rolled bridges)
        // flatten design.length to a bare number. Accepting both the
        // structured object AND the bare scalar means the dimensions
        // row surfaces without a per-server config.
        var vessel = new AisVessel("vessels.test");
        var je = JsonSerializer.SerializeToElement(32.5);
        await Assert.That(vessel.Apply("design.length", je)).IsTrue();
        await Assert.That(vessel.LengthOverallMeters).IsEqualTo(32.5);
    }

    [Test]
    public async Task Apply_DesignLength_ObjectWithoutOverall_NoChange()
    {
        // { hull, waterline } without overall: don't pick a "close
        // enough" sub-key. Stay null so the popup's "no LOA" path
        // renders correctly. Crucially also pin no-clobber: if
        // LengthOverallMeters was already set by a prior good delta
        // and a half-formed object follows, the prior value MUST
        // survive (some SK plugins emit interim {} during a re-publish
        // round). Covered together so a single regression PR fails fast.
        var vessel = new AisVessel("vessels.test");
        var partial = JsonSerializer.SerializeToElement(new { hull = 30.0, waterline = 28.0 });
        await Assert.That(vessel.Apply("design.length", partial)).IsFalse();
        await Assert.That(vessel.LengthOverallMeters).IsNull();

        // Establish a good value, then re-feed the partial: the good
        // value must survive the bad delta.
        var good = JsonSerializer.SerializeToElement(new { overall = 50.0 });
        await Assert.That(vessel.Apply("design.length", good)).IsTrue();
        await Assert.That(vessel.Apply("design.length", partial)).IsFalse();
        await Assert.That(vessel.LengthOverallMeters).IsEqualTo(50.0);
    }

    [Test]
    public async Task Apply_DesignLengthOverall_LeafPath_SetsLoa()
    {
        // SK servers occasionally publish the canonical leaf path
        // directly rather than the structured parent. Both shapes
        // populate the same field; a future case-fall-through that
        // skipped the leaf would leave LOA null on those servers.
        var vessel = new AisVessel("vessels.test");
        var je = JsonSerializer.SerializeToElement(45.0);
        await Assert.That(vessel.Apply("design.length.overall", je)).IsTrue();
        await Assert.That(vessel.LengthOverallMeters).IsEqualTo(45.0);
    }

    [Test]
    public async Task Apply_DesignBeam_Scalar_SetsBeam()
    {
        // Beam is a bare scalar per SK schema (no sub-keys). The popup
        // shows "LOA / Beam" only when both fields populate, so this
        // pin is what unlocks the joint-render branch in aisLayer.js.
        var vessel = new AisVessel("vessels.test");
        var je = JsonSerializer.SerializeToElement(6.2);
        await Assert.That(vessel.Apply("design.beam", je)).IsTrue();
        await Assert.That(vessel.BeamMeters).IsEqualTo(6.2);
    }

    [Test]
    public async Task Apply_IdentityPayload_NestedDesign_FlattensInto_LoaAndBeam()
    {
        // AIS Type 5 (class A static) commonly arrives as an empty-path
        // identity object carrying a nested design subtree. The
        // FlattenAndApply path must walk INTO design.length (object)
        // and design.beam (scalar) so the helm sees dimensions without
        // waiting for separate per-leaf deltas. Some servers never emit
        // the leaves once the nested object has gone out, so this is
        // the only path through which dimensions reach the UI.
        var vessel = new AisVessel("vessels.test");
        var je = JsonSerializer.SerializeToElement(new
        {
            name = "MV STATIC",
            design = new { length = new { overall = 50.0 }, beam = 8.5 }
        });
        await Assert.That(vessel.Apply("", je)).IsTrue();
        await Assert.That(vessel.Name).IsEqualTo("MV STATIC");
        await Assert.That(vessel.LengthOverallMeters).IsEqualTo(50.0);
        await Assert.That(vessel.BeamMeters).IsEqualTo(8.5);
    }

    [Test]
    public async Task Apply_UnknownPath_ReturnsFalse()
    {
        var vessel = new AisVessel("vessels.test");
        var je = JsonSerializer.SerializeToElement(42.0);
        await Assert.That(vessel.Apply("unknown.path", je)).IsFalse();
    }

    [Test]
    public async Task Apply_UpdatesLastSeen()
    {
        var vessel = new AisVessel("vessels.test");
        var before = vessel.LastSeen;
        var je = JsonSerializer.SerializeToElement(3.0);
        vessel.Apply("navigation.speedOverGround", je);
        await Assert.That(vessel.LastSeen).IsGreaterThanOrEqualTo(before);
    }

    // --- ExtractMmsi ---

    [Test]
    [Arguments("vessels.urn:mrn:imo:mmsi:211234567", "211234567")]
    [Arguments("vessels.urn:mrn:imo:mmsi:123456789", "123456789")]
    public async Task ExtractMmsi_ValidContext_ReturnsMmsi(string context, string expected)
    {
        await Assert.That(AisVessel.ExtractMmsi(context)).IsEqualTo(expected);
    }

    [Test]
    public async Task ExtractMmsi_NoMmsiPrefix_ReturnsNull()
    {
        await Assert.That(AisVessel.ExtractMmsi("vessels.some-other-format")).IsNull();
    }
}
