using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Layered coverage for <see cref="AnchorRadiusHeuristic.Suggest"/>:
/// boundary values (null depth, zero, MinValue, MaxValue, NaN, +inf,
/// -inf), the floor + ceiling clamps, scope-multiplier scaling, and
/// the realistic-anchorage scenarios that motivated the helper.
/// </summary>
public class AnchorRadiusHeuristicTests
{
    // --- white-box: scope multiplier + clamp boundaries ---------------

    [Test]
    public async Task NullDepth_FallsBackToLastChosen()
    {
        // Boat at the dock, transducer parked, helm hits Drop. We don't
        // know depth, so we trust whatever radius the helm picked last
        // time (persisted in Settings).
        await Assert.That(AnchorRadiusHeuristic.Suggest(null, 30.0)).IsEqualTo(30);
    }

    [Test]
    public async Task NullDepth_FallsBack_RoundsToWholeMeter()
    {
        // Settings stores a double; we always render whole metres.
        // Math.Round defaults to banker's rounding (half-to-even),
        // which is fine for "rough swing radius" semantics: 32.5
        // rounds to 32, 33.5 rounds to 34. Pin the exact behaviour
        // so a refactor to MidpointRounding.AwayFromZero is a
        // deliberate choice, not an accident.
        await Assert.That(AnchorRadiusHeuristic.Suggest(null, 32.4)).IsEqualTo(32);
        await Assert.That(AnchorRadiusHeuristic.Suggest(null, 32.5)).IsEqualTo(32);
        await Assert.That(AnchorRadiusHeuristic.Suggest(null, 33.5)).IsEqualTo(34);
        await Assert.That(AnchorRadiusHeuristic.Suggest(null, 32.6)).IsEqualTo(33);
    }

    [Test]
    public async Task NullDepth_FallsBack_ClampsToFloor()
    {
        // Corrupted storage / first-run-with-zero: never go below the
        // floor. The chip row's smallest preset is 20 m for a reason.
        await Assert.That(AnchorRadiusHeuristic.Suggest(null, 0.0)).IsEqualTo(20);
        await Assert.That(AnchorRadiusHeuristic.Suggest(null, -10.0)).IsEqualTo(20);
    }

    [Test]
    public async Task PositiveDepth_AppliesScopeMultiplier()
    {
        // 6 m anchorage * 5:1 scope = 30 m radius. Common Lake Geneva /
        // Lago Maggiore depth.
        await Assert.That(AnchorRadiusHeuristic.Suggest(6.0, 30.0)).IsEqualTo(30);
        // 10 m * 5 = 50 m. Common Mediterranean coastal anchorage.
        await Assert.That(AnchorRadiusHeuristic.Suggest(10.0, 30.0)).IsEqualTo(50);
        // 15 m * 5 = 75 m. Deeper bay.
        await Assert.That(AnchorRadiusHeuristic.Suggest(15.0, 30.0)).IsEqualTo(75);
    }

    [Test]
    public async Task ShallowDepth_ClampsToFloor()
    {
        // 2 m * 5 = 10 m raw. Floor pulls it back to 20 m so a wind
        // shift doesn't immediately edge the alarm.
        await Assert.That(AnchorRadiusHeuristic.Suggest(2.0, 30.0)).IsEqualTo(20);
        await Assert.That(AnchorRadiusHeuristic.Suggest(0.5, 30.0)).IsEqualTo(20);
    }

    [Test]
    public async Task DeepDepth_ClampsToCeiling()
    {
        // 50 m * 5 = 250 m raw -- bigger than the chip row's biggest
        // preset (150 m). The 200 m ceiling caps it; a real 50 m
        // anchorage is unusual on small craft and the helm can still
        // tap a chip if they really want 250 m.
        await Assert.That(AnchorRadiusHeuristic.Suggest(50.0, 30.0)).IsEqualTo(200);
        await Assert.That(AnchorRadiusHeuristic.Suggest(100.0, 30.0)).IsEqualTo(200);
    }

    [Test]
    public async Task ExactlyAtFloorBoundary_StaysAtFloor()
    {
        // 4 m * 5 = exactly 20 m. The clamp uses Math.Clamp which is
        // inclusive at the boundary; pin so a refactor to >, not >=,
        // doesn't accidentally drop a legitimate floor-hit value.
        await Assert.That(AnchorRadiusHeuristic.Suggest(4.0, 30.0)).IsEqualTo(20);
    }

    [Test]
    public async Task ExactlyAtCeilingBoundary_StaysAtCeiling()
    {
        await Assert.That(AnchorRadiusHeuristic.Suggest(40.0, 30.0)).IsEqualTo(200);
    }

    // --- equivalence-class boundaries on depth: NaN / infinities / 0 / negative

    [Test]
    public async Task NaNDepth_TreatedAsMissing()
    {
        // Transducer publishing NaN means it powered up but hasn't
        // locked in yet. Treat as missing depth -> fallback path.
        await Assert.That(AnchorRadiusHeuristic.Suggest(double.NaN, 30.0)).IsEqualTo(30);
    }

    [Test]
    public async Task InfiniteDepth_TreatedAsMissing()
    {
        // Both directions of infinity reduce to "transducer fault";
        // never let a bogus reading propose a clamp-ceiling default
        // when the fallback is more trustworthy.
        await Assert.That(AnchorRadiusHeuristic.Suggest(double.PositiveInfinity, 30.0)).IsEqualTo(30);
        await Assert.That(AnchorRadiusHeuristic.Suggest(double.NegativeInfinity, 30.0)).IsEqualTo(30);
    }

    [Test]
    public async Task ZeroDepth_TreatedAsMissing()
    {
        // 0 metres = boat is on the bottom. Either calibration error
        // or the worst news of the day; neither is a useful seed for
        // the alarm radius. Fall back.
        await Assert.That(AnchorRadiusHeuristic.Suggest(0.0, 30.0)).IsEqualTo(30);
    }

    [Test]
    public async Task NegativeDepth_TreatedAsMissing()
    {
        // SK plugins should publish positive metres-below-surface but
        // some publish height-of-water-above-keel and a config swap
        // can flip the sign. Defensive fallback rather than feed a
        // negative through the multiplier.
        await Assert.That(AnchorRadiusHeuristic.Suggest(-3.0, 30.0)).IsEqualTo(30);
    }

    [Test]
    public async Task AbyssalDepth_TreatedAsMissing()
    {
        // > 1000 m: the helm isn't anchoring there. Almost certainly
        // a deep-water transit reading or a transducer that lost
        // bottom return. Don't propose a 5000 m alarm circle.
        await Assert.That(AnchorRadiusHeuristic.Suggest(1500.0, 30.0)).IsEqualTo(30);
    }

    // --- realistic helm scenarios -------------------------------------

    [Test]
    public async Task RealWorld_LakeAnchorageWith4mDepth_Suggests20m()
    {
        // Lake-of-Constance shore: 4 m at the chosen spot, helm has
        // 50 m chain in storage. 5:1 scope = 20 m, lands exactly on
        // the floor and matches the chip row's first preset. One tap
        // = anchored.
        await Assert.That(AnchorRadiusHeuristic.Suggest(4.0, 50.0)).IsEqualTo(20);
    }

    [Test]
    public async Task RealWorld_FirstRunNoDepthYet_FallsBackTo30Default()
    {
        // First time the helm taps Anchor on a brand-new install.
        // Depth hasn't published yet (or no depth plugin); the seeded
        // fallback (Settings.ManualAnchorRadiusMeters default = 30 m)
        // is what they'd have typed by hand anyway.
        await Assert.That(AnchorRadiusHeuristic.Suggest(null, 30.0)).IsEqualTo(30);
    }
}
