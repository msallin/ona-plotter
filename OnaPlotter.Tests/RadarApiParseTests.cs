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
                "brand": "Navico",
                "model": "HALO",
                "name": "HALO 034A",
                "radarIpAddress": "192.168.1.50",
                "spokeDataUrl": "ws://host/signalk/v2/api/vessels/self/radars/nav1034A/spokes",
                "streamUrl": "ws://host/signalk/v1/stream"
              },
              "nav1034B": {
                "brand": "Navico",
                "model": "HALO",
                "name": "HALO 034B",
                "radarIpAddress": "192.168.1.50",
                "spokeDataUrl": "ws://host/signalk/v2/api/vessels/self/radars/nav1034B/spokes",
                "streamUrl": "ws://host/signalk/v1/stream"
              }
            }
            """;

        var list = RadarApi.ParseRadarList(JsonDocument.Parse(json).RootElement);

        await Assert.That(list.Count).IsEqualTo(2);
        await Assert.That(list[0].Id).IsEqualTo("nav1034A");
        await Assert.That(list[0].Brand).IsEqualTo("Navico");
        await Assert.That(list[0].Model).IsEqualTo("HALO");
        await Assert.That(list[0].SpokeDataUrl).Contains("nav1034A/spokes");
        await Assert.That(list[1].Id).IsEqualTo("nav1034B");
    }

    [Test]
    public async Task ParseRadarList_ArrayShape_UsesInlineId()
    {
        // Shape emitted by mayara-server today (what openplotter.local
        // actually serves). Missing spokeDataUrl / streamUrl on this
        // box -- our DTO keeps them nullable so the parser doesn't
        // throw.
        const string json = """
            [
              {
                "id": "nav0231A",
                "name": "HALO 31 A",
                "brand": "Navico",
                "status": "standby",
                "spokesPerRevolution": 2048,
                "maxSpokeLen": 1024,
                "range": 22224
              },
              {
                "id": "nav0231B",
                "name": "HALO 31 B",
                "brand": "Navico",
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
        await Assert.That(list[0].MaxSpokeLen).IsEqualTo(1024);
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
                "nav1": { "name": "HALO A", "brand": "Navico" },
                "nav2": { "name": "HALO B", "brand": "Navico" }
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
                { "id": "nav0231A", "name": "HALO A", "brand": "Navico" }
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
              "nav1": { "name": "HALO A", "brand": "Navico" }
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
        // Wrong shape entirely -- string, number, etc. -- just returns
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
