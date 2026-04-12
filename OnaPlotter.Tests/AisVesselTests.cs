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
