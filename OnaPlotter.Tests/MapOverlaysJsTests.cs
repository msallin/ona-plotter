using Microsoft.JSInterop;
using OnaPlotter.Services.Js;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the IMapOverlaysJs wrapper around the leafletInterop.js chart
/// / weather / coloured-track / server-track surface. Mirrors
/// <see cref="MapAnchorJsTests"/> in shape.
/// </summary>
public class MapOverlaysJsTests
{
    [Test]
    public async Task AddChartLayerAsync_PassesAllSevenArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapOverlaysJs(fake);
        var bounds = new[] { -10.0, 50.0, 10.0, 60.0 };

        await sut.AddChartLayerAsync("c1", "https://tiles/{z}/{x}/{y}.png", 1, 18, 0.8, bounds, 2);

        await Assert.That(fake.Calls[0].id).IsEqualTo("addChartLayer");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(7);
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("c1");
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo("https://tiles/{z}/{x}/{y}.png");
        await Assert.That(fake.Calls[0].args[2]).IsEqualTo(1);
        await Assert.That(fake.Calls[0].args[3]).IsEqualTo(18);
        await Assert.That(fake.Calls[0].args[4]).IsEqualTo(0.8);
        await Assert.That(fake.Calls[0].args[5]).IsSameReferenceAs(bounds);
        await Assert.That(fake.Calls[0].args[6]).IsEqualTo(2);
    }

    [Test]
    public async Task AddChartLayerAsync_AcceptsNullBounds()
    {
        // World-spanning charts pass null Bounds; the wrapper must
        // forward null without throwing.
        var fake = new RecordingJsRef();
        var sut = new MapOverlaysJs(fake);

        await sut.AddChartLayerAsync("c1", "url", 1, 18, 1.0, null, 0);

        await Assert.That(fake.Calls[0].args[5]).IsNull();
        await Assert.That(fake.Calls[0].args[6]).IsEqualTo(0);
    }

    [Test]
    public async Task RemoveChartLayerAsync_PassesId()
    {
        var fake = new RecordingJsRef();
        var sut = new MapOverlaysJs(fake);

        await sut.RemoveChartLayerAsync("c1");

        await Assert.That(fake.Calls[0].id).IsEqualTo("removeChartLayer");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("c1");
    }

    [Test]
    public async Task SetChartLayerOrderAsync_PassesIdsAsSpreadArgs()
    {
        // Match the existing call-site behaviour:
        //   module.InvokeVoidAsync("setChartLayerOrder", order.ToArray())
        // No (object) cast at the call site, so C#'s params object?[]
        // resolution treats string[] as the args array directly --
        // each id arrives as a separate JS argument, NOT as a single
        // array argument. The wrapper preserves that contract; the
        // mismatch with the JS function shape is a pre-existing
        // latent issue at the call site, not something to "fix" here.
        var fake = new RecordingJsRef();
        var sut = new MapOverlaysJs(fake);
        var ids = new[] { "c1", "c2", "c3" };

        await sut.SetChartLayerOrderAsync(ids);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setChartLayerOrder");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(3);
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("c1");
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo("c2");
        await Assert.That(fake.Calls[0].args[2]).IsEqualTo("c3");
    }

    [Test]
    public async Task SetWeatherOverlayAsync_PassesUrlAndOpacity()
    {
        var fake = new RecordingJsRef();
        var sut = new MapOverlaysJs(fake);

        await sut.SetWeatherOverlayAsync("https://radar/{z}/{x}/{y}.png", 0.6);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setWeatherOverlay");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("https://radar/{z}/{x}/{y}.png");
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo(0.6);
    }

    [Test]
    public async Task SetWeatherOverlayOpacityAsync_PassesDouble()
    {
        var fake = new RecordingJsRef();
        var sut = new MapOverlaysJs(fake);

        await sut.SetWeatherOverlayOpacityAsync(0.4);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setWeatherOverlayOpacity");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo(0.4);
    }

    [Test]
    public async Task ClearWeatherOverlayAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapOverlaysJs(fake);

        await sut.ClearWeatherOverlayAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("clearWeatherOverlay");
    }

    [Test]
    public async Task SetColoredTrackAsync_PassesPointsAsSingleArg()
    {
        var fake = new RecordingJsRef();
        var sut = new MapOverlaysJs(fake);
        var points = new[] { new[] { 54.5, 11.2, 5.0 }, new[] { 54.6, 11.3, 4.5 } };

        await sut.SetColoredTrackAsync(points);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setColoredTrack");
        // Cast to (object) keeps the array as a single parameter.
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(1);
        await Assert.That(fake.Calls[0].args[0]).IsSameReferenceAs(points);
    }

    [Test]
    public async Task SetServerTrackAsync_PassesCoordsAndClipFlag()
    {
        var fake = new RecordingJsRef();
        var sut = new MapOverlaysJs(fake);
        var coords = new[] { new[] { 54.5, 11.2 } };

        await sut.SetServerTrackAsync(coords, true);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setServerTrack");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(2);
        await Assert.That(fake.Calls[0].args[0]).IsSameReferenceAs(coords);
        await Assert.That((bool)fake.Calls[0].args[1]!).IsTrue();
    }

    [Test]
    public async Task SetServerTrackClipToBoundsAsync_PassesBool()
    {
        var fake = new RecordingJsRef();
        var sut = new MapOverlaysJs(fake);

        await sut.SetServerTrackClipToBoundsAsync(false);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setServerTrackClipToBounds");
        await Assert.That((bool)fake.Calls[0].args[0]!).IsFalse();
    }

    [Test]
    public async Task ClearServerTrackAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapOverlaysJs(fake);

        await sut.ClearServerTrackAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("clearServerTrack");
    }

    [Test]
    public async Task MarkDisposed_SubsequentCallsAreNoOps()
    {
        var fake = new RecordingJsRef();
        var sut = new MapOverlaysJs(fake);

        sut.MarkDisposed();
        await sut.AddChartLayerAsync("c", "u", 1, 18, 1.0, null, 0);
        await sut.RemoveChartLayerAsync("c");
        await sut.SetWeatherOverlayAsync("u", 1.0);
        await sut.ClearWeatherOverlayAsync();
        await sut.SetColoredTrackAsync(Array.Empty<double[]>());
        await sut.ClearServerTrackAsync();

        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task JsDisconnectedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new MapOverlaysJs(fake);

        await sut.ClearWeatherOverlayAsync();
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ObjectDisposedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisposedNext = true };
        var sut = new MapOverlaysJs(fake);

        await sut.SetServerTrackClipToBoundsAsync(true);
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task JsException_Propagates()
    {
        var fake = new RecordingJsRef { ThrowJsExceptionNext = true };
        var sut = new MapOverlaysJs(fake);

        await Assert.ThrowsAsync<JSException>(() =>
            sut.AddChartLayerAsync("c", "u", 1, 18, 1.0, null, 0));
    }
}
