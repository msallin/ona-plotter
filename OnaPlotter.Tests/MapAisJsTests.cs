using Microsoft.JSInterop;
using OnaPlotter.Services.Js;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the IMapAisJs wrapper around the leafletInterop.js AIS / AtoN
/// surface. Same shape as <see cref="MapAnchorJsTests"/>: happy paths
/// via the recording fake, dispose gate via MarkDisposed, swallow /
/// propagate behaviour via the throw-next flags.
/// </summary>
public class MapAisJsTests
{
    private static OnaPlotter.Services.Map.AisUpdatePayload Update() => new(
        Seq: 1, BaseSeq: OnaPlotter.Services.Map.AisUpdatePayload.FullSnapshot,
        NowUtc: "2026-01-01T12:00:00Z",
        Vessels: [new() { Context = "abc", Lat = 0, Lon = 0 }],
        Removed: []);

    [Test]
    public async Task UpdateAisTargetsAsync_ForwardsUpdateAndReturnsAcceptance()
    {
        var fake = new RecordingJsRef();
        fake.Returns["updateAisTargets"] = true;
        var sut = new MapAisJs(fake);
        var update = Update();

        bool accepted = await sut.UpdateAisTargetsAsync(update);

        await Assert.That(fake.Calls.Count).IsEqualTo(1);
        await Assert.That(fake.Calls[0].id).IsEqualTo("updateAisTargets");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(1);
        await Assert.That(fake.Calls[0].args[0]).IsSameReferenceAs(update);
        await Assert.That(accepted).IsTrue();
    }

    [Test]
    public async Task UpdateAisTargetsAsync_RefusedByRenderer_ReturnsFalse()
    {
        var fake = new RecordingJsRef();
        fake.Returns["updateAisTargets"] = false;
        var sut = new MapAisJs(fake);

        await Assert.That(await sut.UpdateAisTargetsAsync(Update())).IsFalse();
    }

    [Test]
    public async Task UpdateAisTargetsAsync_OnDisconnected_ReturnsFalse()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new MapAisJs(fake);

        await Assert.That(await sut.UpdateAisTargetsAsync(Update())).IsFalse();
    }

    [Test]
    public async Task AisUpdatePayload_SerialisesCompactCamelCaseWire()
    {
        // Pins the renderer contract: camelCase keys, enum roles as
        // lower-case strings, null / default fields omitted so an idle
        // vessel costs only a few bytes.
        var update = new OnaPlotter.Services.Map.AisUpdatePayload(
            Seq: 4, BaseSeq: 3, NowUtc: "2026-01-01T12:00:00Z",
            Vessels:
            [
                new()
                {
                    Context = "v1", Lat = 1.5, Lon = 2.5,
                    MarkerTone = OnaPlotter.Services.Map.AisTone.Danger,
                    VectorTone = OnaPlotter.Services.Map.AisTone.Ship,
                    CpaOverlay = OnaPlotter.Services.Map.AisCpaOverlay.Awareness,
                    LastAisSeen = "2026-01-01T11:59:00Z",
                    TrailAppend = [1.5, 2.5],
                    TrailTrim = 2,
                },
            ],
            Removed: ["v2"]);

        string json = System.Text.Json.JsonSerializer.Serialize(update);
        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
        var v = root.GetProperty("vessels")[0];

        await Assert.That(root.GetProperty("baseSeq").GetInt32()).IsEqualTo(3);
        await Assert.That(root.GetProperty("removed")[0].GetString()).IsEqualTo("v2");
        await Assert.That(v.GetProperty("markerTone").GetString()).IsEqualTo("danger");
        await Assert.That(v.GetProperty("vectorTone").GetString()).IsEqualTo("ship");
        await Assert.That(v.GetProperty("cpaOverlay").GetString()).IsEqualTo("awareness");
        await Assert.That(v.GetProperty("trailTrim").GetInt32()).IsEqualTo(2);
        await Assert.That(v.TryGetProperty("static", out _)).IsFalse();
        await Assert.That(v.TryGetProperty("trail", out _)).IsFalse();
        await Assert.That(v.TryGetProperty("opacity", out _)).IsFalse();
        await Assert.That(v.TryGetProperty("labelVisible", out _)).IsFalse();
        await Assert.That(v.TryGetProperty("cpaNm", out _)).IsFalse();
    }

    [Test]
    public async Task SetAtonsAsync_ForwardsArrayAsSingleArg()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAisJs(fake);
        var atons = new OnaPlotter.Services.Map.AtonPayload[] { new("buoy1", 1, 2, "Lateral", "Port", false, "B1", null, null) };

        await sut.SetAtonsAsync(atons);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setAtons");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(1);
        await Assert.That(fake.Calls[0].args[0]).IsSameReferenceAs(atons);
    }

    [Test]
    public async Task SetAtonsVisibleAsync_PassesBool()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAisJs(fake);

        await sut.SetAtonsVisibleAsync(true);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setAtonsVisible");
        await Assert.That((bool)fake.Calls[0].args[0]!).IsTrue();
    }

    [Test]
    public async Task SetOwnMmsiAsync_PassesString()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAisJs(fake);

        await sut.SetOwnMmsiAsync("123456789");

        await Assert.That(fake.Calls[0].id).IsEqualTo("setOwnMmsi");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("123456789");
    }

    [Test]
    public async Task SetHarborModeAsync_PassesBool()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAisJs(fake);

        await sut.SetHarborModeAsync(false);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setHarborMode");
        await Assert.That((bool)fake.Calls[0].args[0]!).IsFalse();
    }

    [Test]
    public async Task MarkDisposed_SubsequentCallsAreNoOps()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAisJs(fake);

        sut.MarkDisposed();
        await sut.UpdateAisTargetsAsync(Update());
        await sut.SetAtonsAsync([]);
        await sut.SetAtonsVisibleAsync(true);
        await sut.SetOwnMmsiAsync("x");
        await sut.SetHarborModeAsync(true);

        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task JsDisconnectedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new MapAisJs(fake);

        await sut.SetAtonsVisibleAsync(true);
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ObjectDisposedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisposedNext = true };
        var sut = new MapAisJs(fake);

        await sut.SetOwnMmsiAsync("x");
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task JsException_Propagates()
    {
        // A real JS-side regression must surface so it doesn't hide
        // behind the lifecycle swallow.
        var fake = new RecordingJsRef { ThrowJsExceptionNext = true };
        var sut = new MapAisJs(fake);

        await Assert.ThrowsAsync<JSException>(() => sut.SetHarborModeAsync(true));
    }

    [Test]
    public async Task FocusVesselAsync_PassesContextAndReturnsJsResult()
    {
        var fake = new RecordingJsRef();
        fake.Returns["focusVessel"] = true;
        var sut = new MapAisJs(fake);

        bool result = await sut.FocusVesselAsync("vessels.urn:mrn:imo:mmsi:123");

        await Assert.That(fake.Calls[0].id).IsEqualTo("focusVessel");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("vessels.urn:mrn:imo:mmsi:123");
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task FocusVesselAsync_AfterDisposed_ReturnsFalseWithoutCall()
    {
        var fake = new RecordingJsRef();
        var sut = new MapAisJs(fake);
        sut.MarkDisposed();

        bool result = await sut.FocusVesselAsync("anything");

        await Assert.That(result).IsFalse();
        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FocusVesselAsync_OnDisconnected_ReturnsFalse()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new MapAisJs(fake);

        bool result = await sut.FocusVesselAsync("anything");

        await Assert.That(result).IsFalse();
    }
}
