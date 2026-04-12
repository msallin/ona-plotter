using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Tests;

public class AisVesselTests
{
    private readonly AisVessel _vessel = new("vessels.urn:mrn:imo:mmsi:211234567");

    [Fact]
    public void Constructor_SetsContext()
    {
        Assert.Equal("vessels.urn:mrn:imo:mmsi:211234567", _vessel.Context);
    }

    [Fact]
    public void Apply_Position_SetsLatLon()
    {
        var json = JsonSerializer.SerializeToElement(new { latitude = 47.39, longitude = 8.54 });
        Assert.True(_vessel.Apply("navigation.position", json));
        Assert.Equal(47.39, _vessel.Latitude);
        Assert.Equal(8.54, _vessel.Longitude);
    }

    [Fact]
    public void Apply_Position_MissingFields_ReturnsFalse()
    {
        var json = JsonSerializer.SerializeToElement(new { latitude = 47.39 });
        Assert.False(_vessel.Apply("navigation.position", json));
    }

    [Fact]
    public void Apply_SpeedOverGround_SetsProperty()
    {
        var je = JsonSerializer.SerializeToElement(5.0);
        Assert.True(_vessel.Apply("navigation.speedOverGround", je));
        Assert.Equal(5.0, _vessel.SpeedOverGround);
    }

    [Fact]
    public void Apply_CourseOverGround_SetsProperty()
    {
        var je = JsonSerializer.SerializeToElement(1.5);
        Assert.True(_vessel.Apply("navigation.courseOverGroundTrue", je));
        Assert.Equal(1.5, _vessel.CourseOverGround);
    }

    [Fact]
    public void Apply_HeadingTrue_SetsProperty()
    {
        var je = JsonSerializer.SerializeToElement(0.78);
        Assert.True(_vessel.Apply("navigation.headingTrue", je));
        Assert.Equal(0.78, _vessel.Heading);
    }

    [Fact]
    public void Apply_Name_SetsProperty()
    {
        var je = JsonSerializer.SerializeToElement("MV Tester");
        Assert.True(_vessel.Apply("name", je));
        Assert.Equal("MV Tester", _vessel.Name);
    }

    [Fact]
    public void Apply_Mmsi_SetsProperty()
    {
        var je = JsonSerializer.SerializeToElement("211234567");
        Assert.True(_vessel.Apply("mmsi", je));
        Assert.Equal("211234567", _vessel.Mmsi);
    }

    [Fact]
    public void Apply_Callsign_SetsProperty()
    {
        var je = JsonSerializer.SerializeToElement("HB9ABC");
        Assert.True(_vessel.Apply("communication.callsignVhf", je));
        Assert.Equal("HB9ABC", _vessel.Callsign);
    }

    [Fact]
    public void Apply_ShipType_WithNameProperty()
    {
        var je = JsonSerializer.SerializeToElement(new { name = "Cargo" });
        Assert.True(_vessel.Apply("design.aisShipType", je));
        Assert.Equal("Cargo", _vessel.ShipType);
    }

    [Fact]
    public void Apply_UnknownPath_ReturnsFalse()
    {
        var je = JsonSerializer.SerializeToElement(42.0);
        Assert.False(_vessel.Apply("unknown.path", je));
    }

    [Fact]
    public void Apply_UpdatesLastSeen()
    {
        var before = _vessel.LastSeen;
        var je = JsonSerializer.SerializeToElement(3.0);
        _vessel.Apply("navigation.speedOverGround", je);
        Assert.True(_vessel.LastSeen >= before);
    }

    // --- ExtractMmsi ---

    [Theory]
    [InlineData("vessels.urn:mrn:imo:mmsi:211234567", "211234567")]
    [InlineData("vessels.urn:mrn:imo:mmsi:123456789", "123456789")]
    public void ExtractMmsi_ValidContext_ReturnsMmsi(string context, string expected)
    {
        Assert.Equal(expected, AisVessel.ExtractMmsi(context));
    }

    [Fact]
    public void ExtractMmsi_NoMmsiPrefix_ReturnsNull()
    {
        Assert.Null(AisVessel.ExtractMmsi("vessels.some-other-format"));
    }
}
