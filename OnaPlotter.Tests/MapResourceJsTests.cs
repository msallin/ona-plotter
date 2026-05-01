using Microsoft.JSInterop;
using OnaPlotter.Services.Js;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the IMapResourceJs wrapper around the leafletInterop.js
/// waypoint / note / region / circle-preview surface. Mirrors
/// <see cref="MapAnchorJsTests"/> in shape.
/// </summary>
public class MapResourceJsTests
{
    [Test]
    public async Task AddWaypointMarkerAsync_PassesIdLatLonName()
    {
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);

        await sut.AddWaypointMarkerAsync("w1", 54.5, 11.2, "Buoy A", "2026-04-25T12:00:00Z");

        await Assert.That(fake.Calls[0].id).IsEqualTo("addWaypointMarker");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(5);
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("w1");
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo(54.5);
        await Assert.That(fake.Calls[0].args[2]).IsEqualTo(11.2);
        await Assert.That(fake.Calls[0].args[3]).IsEqualTo("Buoy A");
        // Fifth slot is the createdAt ISO string -- pinned by
        // position so a future shape change shows up here, not at
        // runtime as a misplaced popup field.
        await Assert.That(fake.Calls[0].args[4]).IsEqualTo("2026-04-25T12:00:00Z");
    }

    [Test]
    public async Task AddWaypointMarkerAsync_AcceptsNullableCoords()
    {
        // Call sites pass double? straight through after a non-null
        // gate; the wrapper signature must not lose the nullability.
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);
        double? lat = 54.5;
        double? lon = 11.2;

        await sut.AddWaypointMarkerAsync("w1", lat, lon, null, createdAtIso: null);

        await Assert.That(fake.Calls[0].args[1]).IsEqualTo(54.5);
        await Assert.That(fake.Calls[0].args[2]).IsEqualTo(11.2);
        await Assert.That(fake.Calls[0].args[3]).IsNull();
        await Assert.That(fake.Calls[0].args[4]).IsNull();
    }

    [Test]
    public async Task RemoveWaypointMarkerAsync_PassesId()
    {
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);

        await sut.RemoveWaypointMarkerAsync("w1");

        await Assert.That(fake.Calls[0].id).IsEqualTo("removeWaypointMarker");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("w1");
    }

    [Test]
    public async Task AddNoteMarkerAsync_PassesAllSixArgs()
    {
        // Sixth arg is the createdAt ISO string (nullable; null when
        // a third-party note client populated the resource without
        // OnaPlotter's createdAt custom field). Pin the position of
        // every arg so a future shape change shows up here, not at
        // runtime as a silently-misplaced popup field.
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);

        await sut.AddNoteMarkerAsync("n1", 54.5, 11.2, "title", "description", "2026-04-25T12:00:00Z");

        await Assert.That(fake.Calls[0].id).IsEqualTo("addNoteMarker");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(6);
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("n1");
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo(54.5);
        await Assert.That(fake.Calls[0].args[2]).IsEqualTo(11.2);
        await Assert.That(fake.Calls[0].args[3]).IsEqualTo("title");
        await Assert.That(fake.Calls[0].args[4]).IsEqualTo("description");
        await Assert.That(fake.Calls[0].args[5]).IsEqualTo("2026-04-25T12:00:00Z");
    }

    [Test]
    public async Task AddNoteMarkerAsync_NullCreatedAt_ForwardsNull()
    {
        // Notes from Freeboard / KIP / pre-feature OnaPlotter won't
        // have a createdAt; the call must pass null through cleanly
        // (the JS popup formatter renders a dash for null).
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);

        await sut.AddNoteMarkerAsync("n1", 54.5, 11.2, "title", "description", createdAtIso: null);

        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(6);
        await Assert.That(fake.Calls[0].args[5]).IsNull();
    }

    [Test]
    public async Task RemoveNoteMarkerAsync_PassesId()
    {
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);

        await sut.RemoveNoteMarkerAsync("n1");

        await Assert.That(fake.Calls[0].id).IsEqualTo("removeNoteMarker");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("n1");
    }

    [Test]
    public async Task ClearNotesAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);

        await sut.ClearNotesAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("clearNotes");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(0);
    }

    [Test]
    public async Task OpenNotePopupAsync_PassesId()
    {
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);

        await sut.OpenNotePopupAsync("n1");

        await Assert.That(fake.Calls[0].id).IsEqualTo("openNotePopup");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("n1");
    }

    [Test]
    public async Task AddRegionAsync_PassesIdRingsTitleDescription()
    {
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);
        IReadOnlyList<double[][]> rings = new List<double[][]>
        {
            new[] { new[] { 54.5, 11.2 }, new[] { 54.6, 11.3 }, new[] { 54.4, 11.1 } }
        };

        await sut.AddRegionAsync("rg1", rings, "title", "desc");

        await Assert.That(fake.Calls[0].id).IsEqualTo("addRegion");
        await Assert.That(fake.Calls[0].args.Length).IsEqualTo(4);
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("rg1");
        await Assert.That(fake.Calls[0].args[1]).IsSameReferenceAs(rings);
        await Assert.That(fake.Calls[0].args[2]).IsEqualTo("title");
        await Assert.That(fake.Calls[0].args[3]).IsEqualTo("desc");
    }

    [Test]
    public async Task RemoveRegionAsync_PassesId()
    {
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);

        await sut.RemoveRegionAsync("rg1");

        await Assert.That(fake.Calls[0].id).IsEqualTo("removeRegion");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("rg1");
    }

    [Test]
    public async Task FocusRegionAsync_PassesIdAndFirstRing()
    {
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);
        var ring = new[] { new[] { 54.5, 11.2 }, new[] { 54.6, 11.3 } };

        await sut.FocusRegionAsync("rg1", ring);

        await Assert.That(fake.Calls[0].id).IsEqualTo("focusRegion");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo("rg1");
        await Assert.That(fake.Calls[0].args[1]).IsSameReferenceAs(ring);
    }

    [Test]
    public async Task SetCirclePreviewAsync_PassesLatLonRadius()
    {
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);

        await sut.SetCirclePreviewAsync(54.5, 11.2, 250.0);

        await Assert.That(fake.Calls[0].id).IsEqualTo("setCirclePreview");
        await Assert.That(fake.Calls[0].args[0]).IsEqualTo(54.5);
        await Assert.That(fake.Calls[0].args[1]).IsEqualTo(11.2);
        await Assert.That(fake.Calls[0].args[2]).IsEqualTo(250.0);
    }

    [Test]
    public async Task ClearCirclePreviewAsync_NoArgs()
    {
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);

        await sut.ClearCirclePreviewAsync();

        await Assert.That(fake.Calls[0].id).IsEqualTo("clearCirclePreview");
    }

    [Test]
    public async Task MarkDisposed_SubsequentCallsAreNoOps()
    {
        var fake = new RecordingJsRef();
        var sut = new MapResourceJs(fake);

        sut.MarkDisposed();
        await sut.AddWaypointMarkerAsync("w", 0, 0, null, null);
        await sut.RemoveWaypointMarkerAsync("w");
        await sut.AddNoteMarkerAsync("n", 0, 0, null, null, null);
        await sut.ClearNotesAsync();
        await sut.AddRegionAsync("r", new List<double[][]>(), null, null);
        await sut.ClearCirclePreviewAsync();

        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task JsDisconnectedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisconnectedNext = true };
        var sut = new MapResourceJs(fake);

        await sut.RemoveNoteMarkerAsync("n");
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ObjectDisposedException_IsSwallowed()
    {
        var fake = new RecordingJsRef { ThrowDisposedNext = true };
        var sut = new MapResourceJs(fake);

        await sut.OpenNotePopupAsync("n");
        await Assert.That(fake.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task JsException_Propagates()
    {
        var fake = new RecordingJsRef { ThrowJsExceptionNext = true };
        var sut = new MapResourceJs(fake);

        await Assert.ThrowsAsync<JSException>(() =>
            sut.AddWaypointMarkerAsync("w", 0, 0, null, null));
    }
}
