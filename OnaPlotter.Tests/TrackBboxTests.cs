using OnaPlotter.Models;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the geographic-bounds value type. The interesting behaviour
/// is the Pad helper used by the History page's viewport probe;
/// without coverage a future tweak to padding direction or fraction
/// could go undetected.
/// </summary>
public class TrackBboxTests
{
    [Test]
    public async Task Pad_Default20Percent_GrowsBoxOutwardOnEachAxis()
    {
        // 1° × 1° box. 20% padding -> 0.2° per axis -> grows to
        // 1.4° × 1.4° centred on the same midpoint. Pin both halves
        // (south < original south, north > original north).
        var box = new TrackBbox(South: 47.0, West: 8.0, North: 48.0, East: 9.0);
        var padded = box.Pad(0.20);

        await Assert.That(padded.South).IsEqualTo(46.8).Within(1e-9);
        await Assert.That(padded.North).IsEqualTo(48.2).Within(1e-9);
        await Assert.That(padded.West).IsEqualTo(7.8).Within(1e-9);
        await Assert.That(padded.East).IsEqualTo(9.2).Within(1e-9);
    }

    [Test]
    public async Task Pad_ZeroFraction_ReturnsEqualValueBox()
    {
        // Defensive: a 0 fraction is a no-op. The History page
        // currently uses 0.20, but a future test or feature might
        // want to bypass padding without special-casing.
        var box = new TrackBbox(South: 47.0, West: 8.0, North: 48.0, East: 9.0);
        var padded = box.Pad(0);

        await Assert.That(padded).IsEqualTo(box);
    }

    [Test]
    public async Task Pad_DegenerateZeroAreaBox_StillReturnsZeroAreaBox()
    {
        // Zero-area box (south == north and west == east): padding
        // does nothing because the axis spans are zero. Not flagged
        // as an error - a single-point "bbox" is a legitimate
        // degenerate case (e.g. a freshly-mounted History page with
        // no track loaded yet, viewport bounds collapsed to the
        // marker).
        var pt = new TrackBbox(South: 47.4, West: 8.5, North: 47.4, East: 8.5);
        var padded = pt.Pad(0.20);

        await Assert.That(padded).IsEqualTo(pt);
    }

    [Test]
    public async Task Pad_NegativePoleCrossing_DoesNotClamp()
    {
        // A small box near the equator: padding produces a still-
        // sensible non-clamped result. We DON'T clamp to ±90 / ±180
        // because the History page's own bounds come from Leaflet
        // which never crosses the poles, and clamping would silently
        // break the helper for any future caller that does want a
        // raw value. This test pins the no-clamp choice.
        var box = new TrackBbox(South: -1.0, West: -1.0, North: 1.0, East: 1.0);
        var padded = box.Pad(0.20);

        await Assert.That(padded.South).IsEqualTo(-1.4).Within(1e-9);
        await Assert.That(padded.North).IsEqualTo(1.4).Within(1e-9);
    }
}
