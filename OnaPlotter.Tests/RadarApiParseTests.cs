using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pinning tests for the Radar API response parser. Two input shapes
/// matter to us: the spec's dict-keyed-by-id form and the
/// reference-implementation's plain-array form. Capabilities +
/// controls + targets follow the documented schemas verbatim, so
/// we just verify our DTOs round-trip a sample drawn from the spec.
/// </summary>
public class RadarApiParseTests
{
    [Test]
    public async Task ParseRadarList_DictShape_ExtractsIdFromKey()
    {
        // Exact example from docs/develop/rest-api/radar_api.md.
        const string json = """
            {
              "nav1034A": {
                "brand": "AcmeRadar",
                "model": "R200",
                "name": "R200 A",
                "radarIpAddress": "192.168.1.50",
                "spokeDataUrl": "ws://host/signalk/v2/api/vessels/self/radars/nav1034A/spokes",
                "streamUrl": "ws://host/signalk/v1/stream"
              },
              "nav1034B": {
                "brand": "AcmeRadar",
                "model": "R200",
                "name": "R200 B",
                "radarIpAddress": "192.168.1.50",
                "spokeDataUrl": "ws://host/signalk/v2/api/vessels/self/radars/nav1034B/spokes",
                "streamUrl": "ws://host/signalk/v1/stream"
              }
            }
            """;

        var list = RadarApi.ParseRadarList(JsonDocument.Parse(json).RootElement);

        await Assert.That(list.Count).IsEqualTo(2);
        await Assert.That(list[0].Id).IsEqualTo("nav1034A");
        await Assert.That(list[0].Brand).IsEqualTo("AcmeRadar");
        await Assert.That(list[0].Model).IsEqualTo("R200");
        await Assert.That(list[0].SpokeDataUrl).Contains("nav1034A/spokes");
        await Assert.That(list[1].Id).IsEqualTo("nav1034B");
    }

    [Test]
    public async Task ParseRadarList_ArrayShape_UsesInlineId()
    {
        // Shape emitted by mayara-server today (what openplotter.local
        // actually serves). Missing spokeDataUrl / streamUrl on this
        // box - our DTO keeps them nullable so the parser doesn't
        // throw.
        const string json = """
            [
              {
                "id": "nav0231A",
                "name": "R200 31 A",
                "brand": "AcmeRadar",
                "status": "standby",
                "spokesPerRevolution": 2048,
                "maxSpokeLen": 1024,
                "range": 22224
              },
              {
                "id": "nav0231B",
                "name": "R200 31 B",
                "brand": "AcmeRadar",
                "status": "standby",
                "spokesPerRevolution": 2048,
                "maxSpokeLen": 1024,
                "range": 22224
              }
            ]
            """;

        var list = RadarApi.ParseRadarList(JsonDocument.Parse(json).RootElement);

        await Assert.That(list.Count).IsEqualTo(2);
        await Assert.That(list[0].Id).IsEqualTo("nav0231A");
        await Assert.That(list[0].SpokesPerRevolution).IsEqualTo(2048);
        await Assert.That(list[0].MaxSpokeLength).IsEqualTo(1024);
        await Assert.That(list[0].Status).IsEqualTo("standby");
        await Assert.That(list[1].Id).IsEqualTo("nav0231B");
    }

    [Test]
    public async Task ParseRadarList_WrappedShape_UnwrapsVersionEnvelope()
    {
        // Spec TypeScript section: { version, radars: {...} }. The
        // v3.1 reference impl doesn't ship this yet but the spec
        // reserves the right to, so we parse it defensively.
        const string json = """
            {
              "version": "3.1.0",
              "radars": {
                "nav1": { "name": "R200 A", "brand": "AcmeRadar" },
                "nav2": { "name": "R200 B", "brand": "AcmeRadar" }
              }
            }
            """;
        var list = RadarApi.ParseRadarList(JsonDocument.Parse(json).RootElement);
        await Assert.That(list.Count).IsEqualTo(2);
        await Assert.That(list[0].Id).IsEqualTo("nav1");
        await Assert.That(list[1].Id).IsEqualTo("nav2");
    }

    [Test]
    public async Task ParseRadarList_WrappedShape_UnwrapsArrayPayload()
    {
        // Wrapper + inner array is also plausible (old behaviour
        // migrating to wrapped). Must still yield the entries.
        const string json = """
            {
              "version": "3.1.0",
              "radars": [
                { "id": "nav0231A", "name": "R200 A", "brand": "AcmeRadar" }
              ]
            }
            """;
        var list = RadarApi.ParseRadarList(JsonDocument.Parse(json).RootElement);
        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list[0].Id).IsEqualTo("nav0231A");
    }

    [Test]
    public async Task ParseRadarList_DictShape_SkipsNonObjectValues()
    {
        // A server that shoves a version string into the same object
        // as radar entries (poor taste, but observed in the wild)
        // would otherwise cause Deserialize to fail on the string;
        // we skip non-object entries silently.
        const string json = """
            {
              "version": "3.1.0",
              "nav1": { "name": "R200 A", "brand": "AcmeRadar" }
            }
            """;
        var list = RadarApi.ParseRadarList(JsonDocument.Parse(json).RootElement);
        // Note: "version" is not a "radars" property so the unwrap
        // branch doesn't fire; the outer object has one radar-shape
        // entry plus one string-valued entry which we skip.
        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list[0].Id).IsEqualTo("nav1");
    }

    [Test]
    public async Task LegendPixel_NestedObjectValue_DoesNotDesyncReader()
    {
        // Pedantic guard: if a non-conforming server ships a nested
        // object or array as a channel value, we clamp to zero AND
        // advance the reader past the whole value. Previously the
        // reader was left mid-object and the outer loop's EndObject
        // check tripped on the nested brace, dropping subsequent
        // channels. This test pins the fix.
        const string json = """
            { "type": "normal", "color": { "r": { "nested": 5 }, "g": 128, "b": 64 } }
            """;

        var p = JsonSerializer.Deserialize<LegendPixel>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await Assert.That(p).IsNotNull();
        // r clamped to 0 (unparseable); g + b must still land.
        await Assert.That(p!.Color.R).IsEqualTo((byte)0);
        await Assert.That(p.Color.G).IsEqualTo((byte)128);
        await Assert.That(p.Color.B).IsEqualTo((byte)64);
    }

    [Test]
    public async Task ParseRadarList_EmptyInputs_ReturnEmptyList()
    {
        await Assert.That(RadarApi.ParseRadarList(JsonDocument.Parse("[]").RootElement).Count).IsEqualTo(0);
        await Assert.That(RadarApi.ParseRadarList(JsonDocument.Parse("{}").RootElement).Count).IsEqualTo(0);
        // Wrong shape entirely - string, number, etc. - just returns
        // empty rather than throwing so the UI shows "no radars"
        // instead of a crash toast.
        await Assert.That(RadarApi.ParseRadarList(JsonDocument.Parse("\"nope\"").RootElement).Count).IsEqualTo(0);
    }

    [Test]
    public async Task LegendPixel_Color_AcceptsObjectForm()
    {
        // Reference implementation emits the object-colour form; spec
        // documents the hex-string form. Our DTO's converter tolerates
        // both. This test pins the object path.
        const string json = """
            { "type": "normal", "color": { "r": 0, "g": 0, "b": 51, "a": 255 } }
            """;

        var p = JsonSerializer.Deserialize<LegendPixel>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await Assert.That(p).IsNotNull();
        await Assert.That(p!.Type).IsEqualTo("normal");
        await Assert.That(p.Color.R).IsEqualTo((byte)0);
        await Assert.That(p.Color.G).IsEqualTo((byte)0);
        await Assert.That(p.Color.B).IsEqualTo((byte)51);
        await Assert.That(p.Color.A).IsEqualTo((byte)255);
    }

    [Test]
    public async Task LegendPixel_Color_AcceptsHexStringForm()
    {
        // Spec-documented form.
        const string json = """{ "type": "dopplerApproaching", "color": "#FF00FFFF" }""";

        var p = JsonSerializer.Deserialize<LegendPixel>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await Assert.That(p).IsNotNull();
        await Assert.That(p!.Color.R).IsEqualTo((byte)0xFF);
        await Assert.That(p.Color.G).IsEqualTo((byte)0x00);
        await Assert.That(p.Color.B).IsEqualTo((byte)0xFF);
        await Assert.That(p.Color.A).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task LegendPixel_Color_NullDegradesToTransparent()
    {
        // A non-conforming server could ship `"color": null`. The
        // converter must degrade to transparent black (default RadarColor)
        // rather than throw, otherwise one bad pixel bricks legend setup.
        const string json = """{ "type": "normal", "color": null }""";
        var p = JsonSerializer.Deserialize<LegendPixel>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        await Assert.That(p).IsNotNull();
        await Assert.That(p!.Color).IsEqualTo(default(RadarColor));
    }

    [Test]
    public async Task LegendPixel_Color_ArrayFormDegradesToTransparent()
    {
        // RGB-tuple form (`[r, g, b]`) is not in the spec but plausible
        // from a misconfigured provider. The converter must NOT throw
        // on token-kind mismatch - treat as transparent and keep
        // parsing the rest of the legend.
        const string json = """{ "type": "normal", "color": [255, 0, 0] }""";
        var p = JsonSerializer.Deserialize<LegendPixel>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        await Assert.That(p).IsNotNull();
        await Assert.That(p!.Color).IsEqualTo(default(RadarColor));
    }

    [Test]
    public async Task RadarLegend_DopplerArrayForm_DoesNotBreakDeserialisation()
    {
        // Mayara ships dopplerApproaching / dopplerReceding as
        // [startByte, count] arrays, NOT scalars. Earlier the DTO
        // typed them as int? - STJ threw JsonException on the array
        // token, RadarApi.GetCapabilitiesAsync's catch swallowed it
        // and returned null, the JS layer fell back to its default
        // palette, and the operator saw bright blue spokes
        // everywhere (default fallback paints bytes 1-4 as #0000c8).
        // The doppler-band byte indices are intentionally not modelled
        // (see RadarDtos.cs), so STJ skips them and the rest of the
        // legend lands. Pin that the array form is harmless: every
        // other legend field reaches the DTO regardless.
        const string json = """
            {
              "lowReturn": 1,
              "mediumReturn": 5,
              "strongReturn": 10,
              "dopplerApproaching": [17, 1],
              "dopplerReceding": [18, 1],
              "dopplerRain": [19, 1],
              "historyStart": 19,
              "pixelColors": 16,
              "pixels": [
                { "type": "normal", "color": "#000000ff" }
              ]
            }
            """;
        var leg = JsonSerializer.Deserialize<RadarLegend>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        await Assert.That(leg).IsNotNull();
        await Assert.That(leg!.MediumReturn).IsEqualTo(5);
        await Assert.That(leg.HistoryStart).IsEqualTo(19);
        await Assert.That(leg.PixelColors).IsEqualTo(16);
        await Assert.That(leg.Pixels.Length).IsEqualTo(1);
    }

    [Test]
    public async Task RadarCapabilities_DeserialisesFullMayaraResponse()
    {
        // End-to-end pin: the actual openplotter / Mayara
        // /capabilities response previously made GetCapabilitiesAsync
        // return null because of the doppler-array shape. Verify the
        // full document round-trips and the legend lands non-null
        // (the legend null check inside the JS layer's _setLegend was
        // the symptom - bytes 1-4 falling back to default blue).
        const string json = """
            {
              "maxRange": 66672,
              "minRange": 50,
              "supportedRanges": [50, 100, 1852],
              "spokesPerRevolution": 2048,
              "maxSpokeLength": 1024,
              "pixelValues": 16,
              "legend": {
                "dopplerApproaching": [17, 1],
                "dopplerReceding": [18, 1],
                "dopplerRain": null,
                "historyStart": 19,
                "lowReturn": 1,
                "mediumReturn": 5,
                "strongReturn": 10,
                "pixelColors": 16,
                "pixels": [
                  { "type": "normal", "color": "#00000000" },
                  { "type": "normal", "color": "#000033ff" }
                ]
              },
              "hasDoppler": true,
              "hasDualRange": true,
              "hasDualRadar": true,
              "hasSparseSpokes": false,
              "noTransmitSectors": 4,
              "stationary": false,
              "controls": {}
            }
            """;
        var caps = JsonSerializer.Deserialize<RadarCapabilities>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        await Assert.That(caps).IsNotNull();
        await Assert.That(caps!.Legend).IsNotNull();
        await Assert.That(caps.Legend!.MediumReturn).IsEqualTo(5);
        await Assert.That(caps.Legend.Pixels.Length).IsEqualTo(2);
    }

    [Test]
    public async Task LegendPixel_Color_NumberFormDegradesToTransparent()
    {
        // Likewise for a packed integer; not in spec, must not throw.
        const string json = """{ "type": "normal", "color": 16711680 }""";
        var p = JsonSerializer.Deserialize<LegendPixel>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        await Assert.That(p).IsNotNull();
        await Assert.That(p!.Color).IsEqualTo(default(RadarColor));
    }

    [Test]
    public async Task Capabilities_ParsesDocSample()
    {
        const string json = """
            {
              "maxRange": 74080,
              "minRange": 50,
              "supportedRanges": [50, 75, 100, 250, 500, 1000, 2000, 24000, 74080],
              "spokesPerRevolution": 2048,
              "maxSpokeLength": 1024,
              "pixelValues": 16,
              "hasDoppler": true,
              "hasDualRadar": false,
              "hasDualRange": true,
              "hasSparseSpokes": false,
              "noTransmitSectors": 2,
              "controls": {
                "gain": {
                  "id": 4, "name": "Gain", "category": "base",
                  "dataType": "number", "minValue": 0, "maxValue": 100,
                  "stepValue": 1, "hasAuto": true
                }
              },
              "legend": {
                "lowReturn": 1, "mediumReturn": 8, "strongReturn": 13,
                "targetBorder": 17, "dopplerApproaching": 18,
                "dopplerReceding": 19, "historyStart": 20,
                "pixelColors": 16,
                "pixels": [
                  { "type": "normal", "color": { "r": 0, "g": 0, "b": 0, "a": 0 } },
                  { "type": "normal", "color": { "r": 0, "g": 0, "b": 51, "a": 255 } }
                ]
              }
            }
            """;

        var cap = JsonSerializer.Deserialize<RadarCapabilities>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await Assert.That(cap).IsNotNull();
        await Assert.That(cap!.SpokesPerRevolution).IsEqualTo(2048);
        await Assert.That(cap.MaxSpokeLength).IsEqualTo(1024);
        await Assert.That(cap.HasDoppler).IsTrue();
        await Assert.That(cap.Controls).ContainsKey("gain");
        await Assert.That(cap.Controls["gain"].DataType).IsEqualTo("number");
        await Assert.That(cap.Controls["gain"].HasAuto).IsTrue();
        await Assert.That(cap.Legend).IsNotNull();
        await Assert.That(cap.Legend!.PixelColors).IsEqualTo(16);
        await Assert.That(cap.Legend.TargetBorder).IsEqualTo(17);
        await Assert.That(cap.Legend.Pixels.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Target_ParsesDocSample()
    {
        const string json = """
            {
              "id": 1,
              "status": "tracking",
              "position": {
                "bearing": 0.789, "distance": 1852,
                "latitude": 52.3702, "longitude": 4.8952
              },
              "motion": { "course": 3.14159, "speed": 3.34 },
              "danger": { "cpa": 150, "tcpa": 324 },
              "acquisition": "auto",
              "sourceZone": 1,
              "firstSeen": "2025-01-15T10:25:00Z",
              "lastSeen": "2025-01-15T10:30:00Z"
            }
            """;

        var t = JsonSerializer.Deserialize<RadarArpaTarget>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await Assert.That(t).IsNotNull();
        await Assert.That(t!.Id).IsEqualTo(1);
        await Assert.That(t.Status).IsEqualTo("tracking");
        await Assert.That(t.Position!.Bearing).IsEqualTo(0.789);
        await Assert.That(t.Position.Distance).IsEqualTo(1852.0);
        await Assert.That(t.Motion!.Speed).IsEqualTo(3.34);
        await Assert.That(t.Danger!.Cpa).IsEqualTo(150.0);
        await Assert.That(t.Danger.Tcpa).IsEqualTo(324.0);
        await Assert.That(t.SourceZone).IsEqualTo(1);
    }

    [Test]
    public async Task Target_PartialMotionOk()
    {
        // Freshly acquired target: no motion/danger yet.
        const string json = """
            {
              "id": 5, "status": "acquiring",
              "position": { "bearing": 1.2, "distance": 500 },
              "acquisition": "manual",
              "firstSeen": "x", "lastSeen": "x"
            }
            """;
        var t = JsonSerializer.Deserialize<RadarArpaTarget>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        await Assert.That(t).IsNotNull();
        await Assert.That(t!.Motion).IsNull();
        await Assert.That(t.Danger).IsNull();
        await Assert.That(t.Acquisition).IsEqualTo("manual");
    }

    [Test]
    public async Task ControlValue_NumericAccessor_Works()
    {
        const string json = """{"auto":false,"value":58}""";
        var cv = JsonSerializer.Deserialize<ControlValue>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        await Assert.That(cv).IsNotNull();
        await Assert.That(cv!.NumericValue).IsEqualTo(58.0);
        await Assert.That(cv.Auto).IsFalse();
    }

    // Mirror the options bag used by RadarApi.SetControlAsync (see
    // RadarApi.s_json). Reused across the wire-shape tests below so a
    // future tweak to the serialiser options propagates everywhere.
    private static readonly JsonSerializerOptions s_putOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Test]
    public async Task ControlValue_PutBody_IsMinimalSpecShape()
    {
        // Pin the over-the-wire shape for control writes: only the
        // primary value field, no nulls, no typed-accessor leakage.
        // Mayara/SK rejects the bloated body (HTTP 400) when the typed
        // accessors NumericValue / StringValue leak into the JSON, or
        // when ControlValue's optional sector / zone / rect fields
        // serialise as nulls. Both are fixed via [JsonIgnore] on the
        // accessors and WhenWritingNull on the serializer options.
        //
        // Driven through ControlValue.ForRange so the test exercises
        // the actual production factory, not a stand-in. The value
        // carries as a JSON string ("1852") not a number; see
        // ForRange's doc comment for the SK quirk that requires this.
        var wire = JsonSerializer.Serialize(ControlValue.ForRange(1852), s_putOptions);
        await Assert.That(wire).IsEqualTo("""{"value":"1852"}""");
    }

    [Test]
    public async Task ControlValue_ForRange_WireShapeIsCultureInvariant()
    {
        // Lock the contract that the range PUT body does not depend on
        // CurrentCulture. int.ToString() is implicitly culture-invariant
        // for the default ("G") format - so this test passes without
        // the InvariantCulture argument too - but the contract matters:
        // a future refactor that adopts a culture-sensitive format
        // string (e.g. ToString("N0")) would otherwise round-trip
        // "74.080" or "74,080" to a non-en-US helm and the server would
        // reject the value as out of range.
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // de-DE uses "." as thousands separator and "," as decimal,
            // so any leak from CurrentCulture into the wire shape would
            // change the digits or punctuation in the string.
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");
            var wire = JsonSerializer.Serialize(ControlValue.ForRange(74080), s_putOptions);
            await Assert.That(wire).IsEqualTo("""{"value":"74080"}""");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prev;
        }
    }

    [Test]
    public async Task ControlValue_PutBody_OmitsTypedAccessors()
    {
        // Belt-and-braces against the [JsonIgnore] being removed:
        // even WITHOUT WhenWritingNull, the typed accessors must
        // never serialise - they're not wire fields and would
        // confuse spec-conformant servers.
        // Assert on parsed property names rather than substring so a
        // future field whose name contains "NumericValue" / "StringValue"
        // (e.g. "NumericValueAuto") doesn't false-flag.
        var body = new ControlValue { Value = JsonSerializer.SerializeToElement("R200") };
        var defaults = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var wire = JsonSerializer.Serialize(body, defaults);
        using var doc = JsonDocument.Parse(wire);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        await Assert.That(names).DoesNotContain("NumericValue");
        await Assert.That(names).DoesNotContain("StringValue");
    }

    [Test]
    public async Task ControlValue_SectorShape_RoundTrips()
    {
        // value = start angle, endValue = end angle, enabled toggle.
        const string json = """{"enabled":true,"value":-1.5533,"endValue":-1.2217}""";
        var cv = JsonSerializer.Deserialize<ControlValue>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        await Assert.That(cv).IsNotNull();
        await Assert.That(cv!.Enabled).IsTrue();
        await Assert.That(cv.NumericValue).IsEqualTo(-1.5533);
        await Assert.That(cv.EndValue).IsEqualTo(-1.2217);
    }

    [Test]
    public async Task ControlValue_ZoneShape_CapturesDistances()
    {
        const string json = """
            {"enabled":true,"value":-0.5585,"endValue":1.7104,"startDistance":100,"endDistance":232}
            """;
        var cv = JsonSerializer.Deserialize<ControlValue>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        await Assert.That(cv).IsNotNull();
        await Assert.That(cv!.StartDistance).IsEqualTo(100.0);
        await Assert.That(cv.EndDistance).IsEqualTo(232.0);
    }
}
