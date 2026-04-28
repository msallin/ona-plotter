using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class RangeScaleTests
{
    // Reference sample: 200 px wide, 8 px floor (matches the corner
    // chip in leafletInterop.js). The pinch preview uses a 12 px
    // floor; covered by the explicit-floor case below.

    // === ladder picks ===

    [Test]
    public async Task PickFor_AtLadderEntry_PicksThatEntry()
    {
        var pick = RangeScale.PickFor(1.0, 200, 8);
        await Assert.That(pick.NiceNm).IsEqualTo(1.0);
        await Assert.That(pick.Label).IsEqualTo("1 nm");
    }

    [Test]
    public async Task PickFor_BetweenEntries_PicksLowerNeighbour()
    {
        // 0.7 nm sample -> picks 0.5 (largest entry <= 0.7).
        var pick = RangeScale.PickFor(0.7, 200, 8);
        await Assert.That(pick.NiceNm).IsEqualTo(0.5);
        await Assert.That(pick.Label).IsEqualTo("0.5 nm");
    }

    [Test]
    public async Task PickFor_LargeSample_PicksLargestEntry()
    {
        var pick = RangeScale.PickFor(750, 200, 8);
        await Assert.That(pick.NiceNm).IsEqualTo(500);
        await Assert.That(pick.Label).IsEqualTo("500 nm");
    }

    // === sub-floor (zoomed in past 0.02 nm sample) ===

    [Test]
    public async Task PickFor_BelowFloor_PicksFloorButCapsWidthAtSample()
    {
        // Helm zooms tight harbour: 200 px sample = 5 m on the
        // ground = 0.0027 nm. Ladder bottoms at 0.02. Without the
        // width cap the bar would render at 200 * 0.02 / 0.0027
        // = 1481 px -- 7x wider than the reference sample, lying
        // about its own length.
        var pick = RangeScale.PickFor(0.0027, 200, 8);
        await Assert.That(pick.NiceNm).IsEqualTo(0.02);
        await Assert.That(pick.WidthPx).IsEqualTo(200);
    }

    [Test]
    public async Task PickFor_AtFloorExactly_PicksFloorAtFullWidth()
    {
        var pick = RangeScale.PickFor(0.02, 200, 8);
        await Assert.That(pick.NiceNm).IsEqualTo(0.02);
        await Assert.That(pick.WidthPx).IsEqualTo(200);
    }

    // === width math ===

    [Test]
    public async Task PickFor_HalfSample_RendersHalfWidth()
    {
        // 1.7 nm sample sits between ladder entries 1 and 2; picker
        // takes the largest entry <= 1.7 = 1.0; renders at 200 *
        // (1/1.7) ~= 118 px.
        var pick = RangeScale.PickFor(1.7, 200, 8);
        await Assert.That(pick.NiceNm).IsEqualTo(1.0);
        await Assert.That(pick.WidthPx).IsEqualTo(118);
    }

    [Test]
    public async Task PickFor_ExactLadderEntry_RendersFullSampleWidth()
    {
        // Sample is exactly at a ladder entry; the picker returns
        // that entry and the width is the full sample width.
        var pick = RangeScale.PickFor(2.0, 200, 8);
        await Assert.That(pick.NiceNm).IsEqualTo(2.0);
        await Assert.That(pick.WidthPx).IsEqualTo(200);
    }

    [Test]
    public async Task PickFor_HonoursMinPxFloor()
    {
        // Very wide zoom: 200 nm sample, picks 200 nm -> full
        // width. But verify the floor doesn't get violated when
        // the math drives below it.
        // Build a case where the raw width is small: sample = 100,
        // pick = 0.5, raw width = 1 px. minPx 8 should clamp up.
        var pick = RangeScale.PickFor(100.0, 200, 8);
        // Picks 100 (largest <= 100), raw width = 200 * (100/100) = 200.
        // For a real sub-minPx case: sample=2000, picks 500, raw=
        // 200 * 500 / 2000 = 50, above the floor.
        // Need a case where pick / sample is very small.
        // Use sampleWidthPx = 20: sample 100, pick 100, raw=20.
        // Hmm hard to trigger naturally. Force with a tight ratio:
        // sample = 4000 (off the top of the ladder), picks 500,
        // raw = 200 * (500/4000) = 25 -> above floor 8.
        // Pick a more aggressive ratio:
        var p2 = RangeScale.PickFor(20000, 200, 8);
        // picks 500 (largest <= 20000); raw = 200 * 500/20000 = 5.
        // floor 8 should clamp up.
        await Assert.That(p2.WidthPx).IsEqualTo(8);
    }

    [Test]
    public async Task PickFor_HonoursLargerMinPx()
    {
        // Pinch preview uses minPx = 12 instead of 8. Verify the
        // floor parameter is actually honoured.
        var pick = RangeScale.PickFor(20000, 200, 12);
        await Assert.That(pick.WidthPx).IsEqualTo(12);
    }

    // === defensive non-finite / non-positive ===

    [Test]
    public async Task PickFor_ZeroSample_ReturnsFloorAtMinPx()
    {
        var pick = RangeScale.PickFor(0, 200, 8);
        await Assert.That(pick.NiceNm).IsEqualTo(0.02);
        await Assert.That(pick.WidthPx).IsEqualTo(8);
    }

    [Test]
    public async Task PickFor_NegativeSample_ReturnsFloorAtMinPx()
    {
        var pick = RangeScale.PickFor(-1, 200, 8);
        await Assert.That(pick.NiceNm).IsEqualTo(0.02);
        await Assert.That(pick.WidthPx).IsEqualTo(8);
    }

    [Test]
    public async Task PickFor_NaNSample_ReturnsFloorAtMinPx()
    {
        var pick = RangeScale.PickFor(double.NaN, 200, 8);
        await Assert.That(pick.NiceNm).IsEqualTo(0.02);
        await Assert.That(pick.WidthPx).IsEqualTo(8);
    }

    // === label formatting ===

    [Test]
    public async Task PickFor_SubOneLabelTrimsTrailingZeros()
    {
        // 0.5 -> "0.5 nm" (no trailing zero). 0.50 would be ugly.
        var pick = RangeScale.PickFor(0.5, 200, 8);
        await Assert.That(pick.Label).IsEqualTo("0.5 nm");
    }

    [Test]
    public async Task PickFor_SubOneLabelTrimsOrphanDot()
    {
        // 0.10 -> "0.1 nm" (intermediate strips trailing zero,
        // leaving "0.1 ", no orphan dot in this case). The orphan-
        // dot trim matters for cases like 0.20 -> "0.2 nm".
        var pick = RangeScale.PickFor(0.1, 200, 8);
        await Assert.That(pick.Label).IsEqualTo("0.1 nm");
    }

    [Test]
    public async Task PickFor_OneAndAbove_RendersIntegerLabel()
    {
        var pickOne = RangeScale.PickFor(1.0, 200, 8);
        await Assert.That(pickOne.Label).IsEqualTo("1 nm");

        var pickFive = RangeScale.PickFor(5.0, 200, 8);
        await Assert.That(pickFive.Label).IsEqualTo("5 nm");
    }
}
