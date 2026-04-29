using Microsoft.JSInterop;
using OnaPlotter.Services.Js;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the IMapControlsJs wrapper around the leafletInterop.js
/// imperative-map-state surface. Mirrors <see cref="MapAnchorJsTests"/>
/// in shape: representative happy paths, dispose gate, swallow /
/// propagate behaviour. Doesn't pin every no-arg method individually
/// since they share the same InvokeSafe path.
/// </summary>
public class MapControlsJsTests
{
    [Test]
    public async Task SetSignalKBaseUrlAsync_PassesUrl()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        await sut.SetSignalKBaseUrlAsync("https://sk.local:3000");

        await Assert.That(fake.Calls[0].id).IsEqualTo("setSignalKBaseUrl");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("https://sk.local:3000");
    }

    [Test]
    public async Task SetMapOrientationAsync_PassesMode()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        await sut.SetMapOrientationAsync("course");

        await Assert.That(fake.Calls[0].id).IsEqualTo("setMapOrientation");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("course");
    }

    [Test]
    public async Task SetFollowAsync_PassesBool()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        await sut.SetFollowAsync(true);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setFollow");
        await Assert.That((bool)fake.Calls[0].args[0]!).IsTrue();
    }

    [Test]
    public async Task ClearLaylinesAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        await sut.ClearLaylinesAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("clearLaylines");
    }

    [Test]
    public async Task SetNightModeAsync_PassesBool()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        await sut.SetNightModeAsync(true);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setNightMode");
        await Assert.That((bool)fake.Calls[0].args[0]!).IsTrue();
    }

    [Test]
    public async Task SetGuardZoneAsync_PassesAllThreeArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        await sut.SetGuardZoneAsync(0.5, 6.0, 0.7);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setGuardZone");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(3);
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo(0.5);
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo(6.0);
        await Assert.That(fake.Calls[0].args[2]).IsEqualTo(0.7);
    }

    [Test]
    public async Task PanToAsync_PassesLatLon()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        await sut.PanToAsync(54.5, 11.2);

        await Assert.That(fake.Calls[0].id).IsEqualTo("panTo");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo(54.5);
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo(11.2);
    }

    [Test]
    public async Task ZoomToTrackAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        await sut.ZoomToTrackAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("zoomToTrack");
    }

    [Test]
    public async Task FitBoundsAsync_PassesAllFourArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        await sut.FitBoundsAsync(54.0, 11.0, 55.0, 12.0);

        await Assert.That(fake.Calls[0].id).IsEqualTo("fitBounds");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(4);
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo(54.0);
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo(11.0);
        await Assert.That(fake.Calls[0].args[2]).IsEqualTo(55.0);
        await Assert.That(fake.Calls[0].args[3]).IsEqualTo(12.0);
    }

    [Test]
    public async Task EnableKeyboardShortcutsAsync_PassesDotNetRef()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);
        // Concrete reference type just to exercise the generic; the
        // test class itself stands in for "any C# class".
        using var dotNetRef = DotNetObjectReference.Create(this);

        await sut.EnableKeyboardShortcutsAsync(dotNetRef);

        await Assert.That(fake.Calls[0].id).IsEqualTo("enableKeyboardShortcuts");
        await Assert.That(fake.Calls[0].args[0]).IsSameReferenceAs(dotNetRef);
    }

    [Test]
    public async Task DisableKeyboardShortcutsAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        await sut.DisableKeyboardShortcutsAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("disableKeyboardShortcuts");
    }

    [Test]
    public async Task ApplyFrameAsync_PassesFrame()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);
        var frame = new { pos = new { lat = 54.5, lon = 11.2 } };

        await sut.ApplyFrameAsync(frame);

        await Assert.That(fake.Calls[0].id).IsEqualTo("applyFrame");
        await Assert.That(fake.Calls[0].args[0]).IsSameReferenceAs(frame);
    }

    [Test]
    public async Task SetMobAsync_PassesLatLon()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        await sut.SetMobAsync(54.5, 11.2);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setMob");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo(54.5);
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo(11.2);
    }

    [Test]
    public async Task ClearMobAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        await sut.ClearMobAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("clearMob");
    }

    [Test]
    public async Task ClearCurrentArrowAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        await sut.ClearCurrentArrowAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("clearCurrentArrow");
    }

    [Test]
    public async Task MarkDisposed_SubsequentCallsAreNoOps()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);

        sut.MarkDisposed();
        await sut.SetFollowAsync(true);
        await sut.SetNightModeAsync(true);
        await sut.PanToAsync(0, 0);
        await sut.ZoomToTrackAsync();
        await sut.SetMobAsync(0, 0);
        await sut.ClearMobAsync();

        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task JsDisconnectedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new MapControlsJs(fake);

        await sut.SetFollowAsync(true);
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ObjectDisposedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisposedNext = true };
        var sut = new MapControlsJs(fake);

        await sut.SetNightModeAsync(true);
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task JsException_Propagates()
    {
        var fake = new RecordingJsRef { ThrowJsExceptionNext = true };
        var sut = new MapControlsJs(fake);

        await Assert.ThrowsAsync<JSException>(() => sut.PanToAsync(0, 0));
    }

    [Test]
    public async Task GetMapCenterAsync_ReturnsJsPayloadAsIs()
    {
        var fake = new RecordingJsRef();
        fake.Returns["getMapCenter"] = new double[] { 54.5, 11.2 };
        var sut = new MapControlsJs(fake);

        var result = await sut.GetMapCenterAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("getMapCenter");
        await Assert.That(result).IsNotNull();
        await Assert.That(result![0]).IsEqualTo(54.5);
        await Assert.That(result[1]).IsEqualTo(11.2);
    }

    [Test]
    public async Task GetMapCenterAsync_AfterDisposed_ReturnsNullWithoutCall()
    {
        var fake = new RecordingJsRef();
        var sut = new MapControlsJs(fake);
        sut.MarkDisposed();

        var result = await sut.GetMapCenterAsync();

        await Assert.That(result).IsNull();
        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetMapCenterAsync_OnDisconnected_ReturnsNull()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new MapControlsJs(fake);

        var result = await sut.GetMapCenterAsync();

        await Assert.That(result).IsNull();
    }
}
