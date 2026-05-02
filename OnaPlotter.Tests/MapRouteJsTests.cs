using Microsoft.JSInterop;
using OnaPlotter.Services.Js;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the IMapRouteJs wrapper around the leafletInterop.js route +
/// active-route surface. Mirrors <see cref="MapAnchorJsTests"/> in
/// shape: happy paths via the recording fake, dispose gate via
/// MarkDisposed, swallow / propagate via the throw-next flags.
/// </summary>
public class MapRouteJsTests
{
    [Test]
    public async Task AddRouteAsync_PassesIdNameCoordsAndTotalNm()
    {
        var fake = new RecordingJsRef();
        var sut = new MapRouteJs(fake);
        var coords = new[] { new[] { 54.5, 11.2 }, new[] { 54.6, 11.3 } };

        await sut.AddRouteAsync("r1", "Plan A", coords, totalNm: 6.4);

        await Assert.That(fake.Calls[0].id).IsEqualTo("addRoute");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(4);
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("r1");
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo("Plan A");
        await Assert.That(fake.Calls[0].args[2]).IsSameReferenceAs(coords);
        // totalNm now travels alongside coords -- the JS popup uses
        // it directly instead of recomputing the haversine sum.
        await Assert.That(fake.Calls[0].args[3]).IsEqualTo(6.4);
    }

    [Test]
    public async Task RemoveRouteAsync_PassesId()
    {
        var fake = new RecordingJsRef();
        var sut = new MapRouteJs(fake);

        await sut.RemoveRouteAsync("r1");

        await Assert.That(fake.Calls[0].id).IsEqualTo("removeRoute");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("r1");
    }

    [Test]
    public async Task SetActiveRouteAsync_PassesAllArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapRouteJs(fake);
        var coords = new[] { new[] { 54.5, 11.2 } };

        await sut.SetActiveRouteAsync(coords, 2, "rid", "rname");

        await Assert.That(fake.Calls[0].id).IsEqualTo("setActiveRoute");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(4);
        // First arg is the coords array passed as a single object so
        // the JS side reads it as one parameter, not flattened.
        await Assert.That(fake.Calls[0].args[0]).IsSameReferenceAs(coords);
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo(2);
        await Assert.That(fake.Calls[0].args[2]).IsEqualTo("rid");
        await Assert.That(fake.Calls[0].args[3]).IsEqualTo("rname");
    }

    [Test]
    public async Task ClearActiveRouteAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapRouteJs(fake);

        await sut.ClearActiveRouteAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("clearActiveRoute");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SetActiveOverlayHiddenAsync_PassesBool()
    {
        var fake = new RecordingJsRef();
        var sut = new MapRouteJs(fake);

        await sut.SetActiveOverlayHiddenAsync(true);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setActiveOverlayHidden");
        await Assert.That((bool)fake.Calls[0].args[0]!).IsTrue();
    }

    [Test]
    public async Task SetActiveRouteStoppingAsync_PassesBool()
    {
        var fake = new RecordingJsRef();
        var sut = new MapRouteJs(fake);

        await sut.SetActiveRouteStoppingAsync(false);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setActiveRouteStopping");
        await Assert.That((bool)fake.Calls[0].args[0]!).IsFalse();
    }

    [Test]
    public async Task SetActiveRouteTtgSecondsAsync_PassesNullableDouble()
    {
        var fake = new RecordingJsRef();
        var sut = new MapRouteJs(fake);

        await sut.SetActiveRouteTtgSecondsAsync(123.4);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setActiveRouteTtgSeconds");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo(123.4);
    }

    [Test]
    public async Task SetActiveRouteTtgSecondsAsync_NullClearsRow()
    {
        // Null lat/lon paths in the call site clear the popup ETA row;
        // the wrapper must forward null through unchanged.
        var fake = new RecordingJsRef();
        var sut = new MapRouteJs(fake);

        await sut.SetActiveRouteTtgSecondsAsync(null);

        await Assert.That(fake.Calls[0].args[0]).IsNull();
    }

    [Test]
    public async Task ClearCourseLineAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapRouteJs(fake);

        await sut.ClearCourseLineAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("clearCourseLine");
    }

    [Test]
    public async Task MarkDisposed_SubsequentCallsAreNoOps()
    {
        var fake = new RecordingJsRef();
        var sut = new MapRouteJs(fake);

        sut.MarkDisposed();
        await sut.AddRouteAsync("r", null, new[] { new[] { 0.0, 0.0 } }, totalNm: 0);
        await sut.RemoveRouteAsync("r");
        await sut.ClearActiveRouteAsync();

        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task JsDisconnectedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new MapRouteJs(fake);

        await sut.ClearActiveRouteAsync();
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ObjectDisposedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisposedNext = true };
        var sut = new MapRouteJs(fake);

        await sut.SetActiveOverlayHiddenAsync(true);
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task JsException_Propagates()
    {
        var fake = new RecordingJsRef { ThrowJsExceptionNext = true };
        var sut = new MapRouteJs(fake);

        await Assert.ThrowsAsync<JSException>(() => sut.RemoveRouteAsync("r"));
    }
}
