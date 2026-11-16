using OnaPlotter.Services.Js;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the HistoryMapJs wrapper around historyMapLayer.js: export names,
/// argument order, the teardown swallow, and the bounds / base-layer
/// helpers C# owns so the JS stays a renderer.
/// </summary>
public class HistoryMapJsTests
{
    [Test]
    public async Task InitAsync_PassesElementCentreZoomLayersAndRef()
    {
        var fake = new RecordingJsRef();
        var sut = new HistoryMapJs(fake);
        var dotNet = new object();

        await sut.InitAsync("historyMapDiv", 47.4, 8.5, 13, HistoryMapJs.DefaultBaseLayers, dotNet);

        var (id, args) = fake.Calls[0];
        await Assert.That(id).IsEqualTo("initHistoryMap");
        await Assert.That(args.Length).IsEqualTo(6);
        await Assert.That(args[0]).IsEqualTo("historyMapDiv");
        await Assert.That(args[1]).IsEqualTo(47.4);
        await Assert.That(args[2]).IsEqualTo(8.5);
        await Assert.That(args[3]).IsEqualTo(13);
        await Assert.That(args[4]).IsSameReferenceAs(HistoryMapJs.DefaultBaseLayers);
        await Assert.That(args[5]).IsSameReferenceAs(dotNet);
    }

    [Test]
    public async Task VoidCalls_UseMatchingExportNames()
    {
        var fake = new RecordingJsRef();
        var sut = new HistoryMapJs(fake);
        var segs = new List<HistorySegmentRender.SegmentPayload>();

        await sut.DisposeMapAsync();
        await sut.SetViewAsync(1, 2, 14);
        await sut.SetBoatMarkerAsync(1, 2, HistorySegmentRender.BoatColor);
        await sut.AddChartLayerAsync("c", "u", 1, 18, 0.85, null);
        await sut.RemoveChartLayerAsync("c");
        await sut.RenderSegmentsAsync(segs, true);
        await sut.FitSegmentAsync(3);
        await sut.SetPlaybackMarkerAsync(1, 2, HistorySegmentRender.PlaybackColor);
        await sut.MovePlaybackMarkerAsync(1, 2);

        await Assert.That(fake.Calls.Select(c => c.id)).IsEquivalentTo(new[]
        {
            "disposeHistoryMap", "setView", "setBoatMarker", "addChartLayer",
            "removeChartLayer", "renderSegments", "fitSegment",
            "setPlaybackMarker", "movePlaybackMarker",
        });
        // renderSegments must receive the list as ONE argument, not spread.
        var render = fake.Calls.Single(c => c.id == "renderSegments");
        await Assert.That(render.args.Length).IsEqualTo(2);
        await Assert.That(render.args[0]).IsSameReferenceAs(segs);
        await Assert.That((bool)render.args[1]!).IsTrue();
        var chart = fake.Calls.Single(c => c.id == "addChartLayer");
        await Assert.That(chart.args.Length).IsEqualTo(6);
        await Assert.That(chart.args[5]).IsNull();
    }

    [Test]
    public async Task Disconnected_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new HistoryMapJs(fake);

        await sut.MovePlaybackMarkerAsync(1, 2);

        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ObjectDisposed_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisposedNext = true };
        var sut = new HistoryMapJs(fake);

        await sut.FitSegmentAsync(0);

        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AfterMarkDisposed_NothingIsSent()
    {
        var fake = new RecordingJsRef();
        var sut = new HistoryMapJs(fake);
        sut.MarkDisposed();

        await sut.SetViewAsync(1, 2, 3);
        var bounds = await sut.GetViewportBoundsAsync();

        await Assert.That(fake.Calls.Count).IsEqualTo(0);
        await Assert.That(bounds).IsNull();
    }

    [Test]
    public async Task GetViewportBounds_ReturnsFiniteFourTuple()
    {
        var fake = new RecordingJsRef();
        fake.Returns["getViewportBounds"] = new[] { 47.0, 8.0, 48.0, 9.0 };
        var sut = new HistoryMapJs(fake);

        var bounds = await sut.GetViewportBoundsAsync();

        await Assert.That(bounds).IsEquivalentTo(new[] { 47.0, 8.0, 48.0, 9.0 });
    }

    [Test]
    [Arguments(null)]
    [Arguments(new[] { 1.0, 2.0, 3.0 })]
    [Arguments(new[] { 1.0, double.NaN, 3.0, 4.0 })]
    public async Task GetViewportBounds_RejectsMalformed(double[]? raw)
    {
        var fake = new RecordingJsRef();
        fake.Returns["getViewportBounds"] = raw;
        var sut = new HistoryMapJs(fake);

        await Assert.That(await sut.GetViewportBoundsAsync()).IsNull();
    }

    [Test]
    public async Task GetViewportBounds_JsException_ReturnsNull()
    {
        // Map not initialised yet when the chart picker opens.
        var fake = new RecordingJsRef { ThrowJsExceptionNext = true };
        var sut = new HistoryMapJs(fake);

        await Assert.That(await sut.GetViewportBoundsAsync()).IsNull();
    }

    [Test]
    public async Task ToLeafletBounds_ReordersWestSouthEastNorth()
    {
        // A Solent chart: west -1.6, south 50.6, east -0.9, north 50.9.
        var b = HistoryMapJs.ToLeafletBounds([-1.6, 50.6, -0.9, 50.9]);

        await Assert.That(b).IsNotNull();
        await Assert.That(b![0]).IsEquivalentTo(new[] { 50.6, -1.6 });
        await Assert.That(b[1]).IsEquivalentTo(new[] { 50.9, -0.9 });
    }

    [Test]
    [Arguments(null)]
    [Arguments(new double[0])]
    [Arguments(new[] { 1.0, 2.0, 3.0 })]
    [Arguments(new[] { 1.0, 2.0, 3.0, double.PositiveInfinity })]
    [Arguments(new[] { double.NaN, 2.0, 3.0, 4.0 })]
    public async Task ToLeafletBounds_NullForMissingOrNonFinite(double[]? raw)
    {
        await Assert.That(HistoryMapJs.ToLeafletBounds(raw)).IsNull();
    }

    [Test]
    public async Task DefaultBaseLayers_ComeFromBuiltInCharts()
    {
        var layers = HistoryMapJs.DefaultBaseLayers;

        await Assert.That(layers.Count).IsEqualTo(BuiltInCharts.All.Count);
        await Assert.That(layers[0].Url).IsEqualTo(BuiltInCharts.All[0].Url!);
        await Assert.That(layers.All(l => l.Url.StartsWith("https://", StringComparison.Ordinal))).IsTrue();
        await Assert.That(layers.All(l => l.Opacity is > 0 and <= 1)).IsTrue();
    }
}
