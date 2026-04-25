using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Tests;

public class AtoNTests
{
    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task Constructor_ExtractsMmsiFromContext()
    {
        var a = new AtoN("atons.urn:mrn:imo:mmsi:992111234");
        await Assert.That(a.Mmsi).IsEqualTo("992111234");
    }

    [Test]
    public async Task Constructor_NoMmsiOnNonStandardContext()
    {
        // Some plugins use custom identifier schemes (e.g. local
        // OpenSeaMap mark ids). Mmsi should be null rather than crash.
        var a = new AtoN("atons.local-buoy-7");
        await Assert.That(a.Mmsi).IsNull();
    }

    [Test]
    public async Task ApplyName_SetsName()
    {
        var a = new AtoN("atons.urn:mrn:imo:mmsi:992111234");
        bool changed = a.Apply("name", Parse("\"BUOY 17\""));
        await Assert.That(changed).IsTrue();
        await Assert.That(a.Name).IsEqualTo("BUOY 17");
    }

    [Test]
    public async Task ApplyName_Idempotent()
    {
        // Re-applying the same value is a no-op so the UI doesn't
        // redraw on every tick. Returns false to signal "no change".
        var a = new AtoN("atons.urn:mrn:imo:mmsi:992111234");
        a.Apply("name", Parse("\"BUOY 17\""));
        bool secondChanged = a.Apply("name", Parse("\"BUOY 17\""));
        await Assert.That(secondChanged).IsFalse();
    }

    [Test]
    public async Task ApplyPosition_SetsLatLon()
    {
        var a = new AtoN("atons.urn:mrn:imo:mmsi:992111234");
        bool changed = a.Apply("navigation.position",
            Parse("{\"latitude\":47.5, \"longitude\":-122.25}"));
        await Assert.That(changed).IsTrue();
        await Assert.That(a.Latitude).IsEqualTo(47.5);
        await Assert.That(a.Longitude).IsEqualTo(-122.25);
    }

    [Test]
    public async Task ApplyPosition_MissingFields_NoChange()
    {
        // Malformed position payload: no lat/lon, just an empty object.
        // Returns false and doesn't write garbage to lat/lon.
        var a = new AtoN("atons.urn:mrn:imo:mmsi:992111234");
        bool changed = a.Apply("navigation.position", Parse("{}"));
        await Assert.That(changed).IsFalse();
        await Assert.That(a.Latitude).IsNull();
    }

    [Test]
    public async Task ApplyAtonType_ObjectForm_SetsIdAndName()
    {
        var a = new AtoN("atons.urn:mrn:imo:mmsi:992111234");
        bool changed = a.Apply("atonType",
            Parse("{\"id\":14, \"name\":\"Lateral Starboard\"}"));
        await Assert.That(changed).IsTrue();
        await Assert.That(a.TypeId).IsEqualTo(14);
        await Assert.That(a.TypeName).IsEqualTo("Lateral Starboard");
    }

    [Test]
    public async Task ApplyAtonType_NumberForm_SetsIdOnly()
    {
        // Some plugins publish the bare number under atonType instead
        // of the canonical {id, name} object. The handler should
        // accept both.
        var a = new AtoN("atons.urn:mrn:imo:mmsi:992111234");
        bool changed = a.Apply("atonType", Parse("9"));
        await Assert.That(changed).IsTrue();
        await Assert.That(a.TypeId).IsEqualTo(9);
        await Assert.That(a.TypeName).IsNull();
    }

    [Test]
    public async Task ApplyVirtual_BoolValue_Sets()
    {
        var a = new AtoN("atons.urn:mrn:imo:mmsi:992111234");
        a.Apply("virtual", Parse("true"));
        await Assert.That(a.Virtual).IsTrue();
        a.Apply("virtual", Parse("false"));
        await Assert.That(a.Virtual).IsFalse();
    }

    [Test]
    public async Task ApplyIdentityBundle_FlattensToAllFields()
    {
        // SignalK servers emit a partial identity bundle on the
        // empty path with the AIS Type 21 static data folded into one
        // object. Make sure the bundle is unpacked into the same
        // typed fields the per-leaf paths set.
        var bundle = Parse(@"{
            ""name"": ""WRECK CAUTION"",
            ""mmsi"": ""992111234"",
            ""atonType"": { ""id"": 28, ""name"": ""Isolated Danger"" },
            ""virtual"": true,
            ""navigation"": { ""position"": { ""latitude"": 51.5, ""longitude"": 1.0 } }
        }");
        var a = new AtoN("atons.urn:mrn:imo:mmsi:992111234");
        bool changed = a.Apply("", bundle);

        await Assert.That(changed).IsTrue();
        await Assert.That(a.Name).IsEqualTo("WRECK CAUTION");
        await Assert.That(a.TypeId).IsEqualTo(28);
        await Assert.That(a.TypeName).IsEqualTo("Isolated Danger");
        await Assert.That(a.Virtual).IsTrue();
        await Assert.That(a.Latitude).IsEqualTo(51.5);
        await Assert.That(a.Longitude).IsEqualTo(1.0);
    }

    [Test]
    public async Task UnknownPath_StoredInPropertiesBag()
    {
        // A plugin publishes some custom path we don't model. It should
        // land in Properties so the popover can still show it without
        // a code change.
        var a = new AtoN("atons.urn:mrn:imo:mmsi:992111234");
        a.Apply("plugin.custom.field", Parse("\"hello\""));
        await Assert.That(a.Properties.ContainsKey("plugin.custom.field")).IsTrue();
    }

    [Test]
    public async Task ExtractMmsi_WellFormed()
    {
        await Assert.That(AtoN.ExtractMmsi("atons.urn:mrn:imo:mmsi:992111234"))
            .IsEqualTo("992111234");
    }

    [Test]
    public async Task ExtractMmsi_NoMmsiSegment_ReturnsNull()
    {
        await Assert.That(AtoN.ExtractMmsi("atons.local-buoy-7")).IsNull();
        await Assert.That(AtoN.ExtractMmsi("atons.")).IsNull();
        await Assert.That(AtoN.ExtractMmsi("")).IsNull();
    }
}
