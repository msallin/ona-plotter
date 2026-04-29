using Microsoft.JSInterop;
using OnaPlotter.Services.Js;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the IMapEditJs wrapper around the leafletInterop.js
/// route-edit, polygon-edit, and measure-tool surface. Mirrors
/// <see cref="MapAnchorJsTests"/> in shape: representative happy
/// paths via the recording fake, dispose gate via MarkDisposed,
/// swallow / propagate via the throw-next flags. Doesn't pin every
/// no-arg method individually since the lifecycle calls share the
/// same InvokeSafe path.
/// </summary>
public class MapEditJsTests
{
    [Test]
    public async Task StartRouteEditAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapEditJs(fake);

        await sut.StartRouteEditAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("startRouteEdit");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(0);
    }

    [Test]
    public async Task StopRouteEditAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapEditJs(fake);

        await sut.StopRouteEditAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("stopRouteEdit");
    }

    [Test]
    public async Task LoadRouteForEditAsync_PassesCoordsAsSingleArg()
    {
        var fake = new RecordingJsRef();
        var sut = new MapEditJs(fake);
        var coords = new[] { new[] { 54.5, 11.2 } };

        await sut.LoadRouteForEditAsync(coords);

        await Assert.That(fake.Calls[0].id).IsEqualTo("loadRouteForEdit");
        // (object) cast in the impl ensures the array lands as one
        // parameter, not as N spread args.
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(1);
        await Assert.That(fake.Calls[0].args[0]).IsSameReferenceAs(coords);
    }

    [Test]
    public async Task RemoveRouteEditWaypointAsync_PassesIndex()
    {
        var fake = new RecordingJsRef();
        var sut = new MapEditJs(fake);

        await sut.RemoveRouteEditWaypointAsync(3);

        await Assert.That(fake.Calls[0].id).IsEqualTo("removeRouteEditWaypoint");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo(3);
    }

    [Test]
    public async Task ReverseEditRouteAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapEditJs(fake);

        await sut.ReverseEditRouteAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("reverseEditRoute");
    }

    [Test]
    public async Task LoadPolygonForEditAsync_PassesCoordsAsSingleArg()
    {
        var fake = new RecordingJsRef();
        var sut = new MapEditJs(fake);
        var coords = new[] { new[] { 54.5, 11.2 }, new[] { 54.6, 11.3 }, new[] { 54.4, 11.1 } };

        await sut.LoadPolygonForEditAsync(coords);

        await Assert.That(fake.Calls[0].id).IsEqualTo("loadPolygonForEdit");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(1);
        await Assert.That(fake.Calls[0].args[0]).IsSameReferenceAs(coords);
    }

    [Test]
    public async Task SetMeasureModeAsync_PassesBool()
    {
        var fake = new RecordingJsRef();
        var sut = new MapEditJs(fake);

        await sut.SetMeasureModeAsync(true);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setMeasureMode");
        await Assert.That((bool)fake.Calls[0].args[0]!).IsTrue();
    }

    [Test]
    public async Task MeasureFromVesselToAsync_PassesLatLon()
    {
        var fake = new RecordingJsRef();
        var sut = new MapEditJs(fake);

        await sut.MeasureFromVesselToAsync(54.5, 11.2);

        await Assert.That(fake.Calls[0].id).IsEqualTo("measureFromVesselTo");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo(54.5);
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo(11.2);
    }

    [Test]
    public async Task RemovePolygonEditVertexAsync_PassesIndex()
    {
        var fake = new RecordingJsRef();
        var sut = new MapEditJs(fake);

        await sut.RemovePolygonEditVertexAsync(0);

        await Assert.That(fake.Calls[0].id).IsEqualTo("removePolygonEditVertex");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo(0);
    }

    [Test]
    public async Task MarkDisposed_SubsequentCallsAreNoOps()
    {
        var fake = new RecordingJsRef();
        var sut = new MapEditJs(fake);

        sut.MarkDisposed();
        await sut.StartRouteEditAsync();
        await sut.StopRouteEditAsync();
        await sut.StartPolygonEditAsync();
        await sut.StopPolygonEditAsync();
        await sut.UndoLastPolygonVertexAsync();
        await sut.SetMeasureModeAsync(true);

        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task JsDisconnectedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new MapEditJs(fake);

        await sut.SetMeasureModeAsync(true);
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ObjectDisposedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisposedNext = true };
        var sut = new MapEditJs(fake);

        await sut.UndoLastPolygonVertexAsync();
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task JsException_Propagates()
    {
        var fake = new RecordingJsRef { ThrowJsExceptionNext = true };
        var sut = new MapEditJs(fake);

        await Assert.ThrowsAsync<JSException>(() => sut.StartRouteEditAsync());
    }

    [Test]
    public async Task GetEditRouteStatsAsync_ReturnsJsPayloadAsIs()
    {
        var fake = new RecordingJsRef();
        fake.Returns["getEditRouteStats"] = new double[] { 5, 12.7 };
        var sut = new MapEditJs(fake);

        var stats = await sut.GetEditRouteStatsAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("getEditRouteStats");
        await Assert.That(stats).IsNotNull();
        await Assert.That(stats!.Length).IsEqualTo(2);
        await Assert.That(stats[0]).IsEqualTo(5);
        await Assert.That(stats[1]).IsEqualTo(12.7);
    }

    [Test]
    public async Task GetEditRouteCoordsAsync_ReturnsJsPayloadAsIs()
    {
        var fake = new RecordingJsRef();
        var coords = new double[][] { new[] { 54.5, 11.2 }, new[] { 54.6, 11.3 } };
        fake.Returns["getEditRouteCoords"] = coords;
        var sut = new MapEditJs(fake);

        var result = await sut.GetEditRouteCoordsAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("getEditRouteCoords");
        await Assert.That(result).IsSameReferenceAs(coords);
    }

    [Test]
    public async Task GetPolygonEditCoordsAsync_ReturnsJsPayloadAsIs()
    {
        var fake = new RecordingJsRef();
        var coords = new double[][] { new[] { 54.5, 11.2 }, new[] { 54.6, 11.3 }, new[] { 54.4, 11.1 } };
        fake.Returns["getPolygonEditCoords"] = coords;
        var sut = new MapEditJs(fake);

        var result = await sut.GetPolygonEditCoordsAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("getPolygonEditCoords");
        await Assert.That(result).IsSameReferenceAs(coords);
    }

    [Test]
    public async Task GetEditRouteCoordsAsync_AfterDisposed_ReturnsNullWithoutCall()
    {
        var fake = new RecordingJsRef();
        var sut = new MapEditJs(fake);
        sut.MarkDisposed();

        var result = await sut.GetEditRouteCoordsAsync();

        await Assert.That(result).IsNull();
        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetPolygonEditCoordsAsync_OnDisconnected_ReturnsNull()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new MapEditJs(fake);

        var result = await sut.GetPolygonEditCoordsAsync();

        await Assert.That(result).IsNull();
    }
}
