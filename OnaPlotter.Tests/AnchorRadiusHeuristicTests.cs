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

    // --- lastChosenMeters boundary defence ---------------------------
    // Storage corruption (private-browsing fallthrough, manual
    // edit of localStorage, future migration glitch) can produce
    // NaN / +/-Infinity in the persisted radius. Pin the behaviour
    // so a refactor that drops the IsNaN/IsInfinity guard surfaces.

    [Test]
    public async Task NullDepth_NaNLastChosen_LandsOnFloor()
    {
        await Assert.That(AnchorRadiusHeuristic.Suggest(null, double.NaN))
            .IsEqualTo(AnchorRadiusHeuristic.MinSuggestedMeters);
    }

    [Test]
    public async Task NullDepth_PositiveInfinityLastChosen_LandsOnCeiling()
    {
        // +Infinity -> the helm previously typed something insane
        // OR storage is corrupted. Cap at the ceiling (Max) so the
        // panel still has a usable preset to render rather than
        // proposing int.MaxValue (which would render as "2147483647 m"
        // and snap-up to the largest preset only by accident).
        await Assert.That(AnchorRadiusHeuristic.Suggest(null, double.PositiveInfinity))
            .IsEqualTo(AnchorRadiusHeuristic.MaxSuggestedMeters);
    }

    [Test]
    public async Task NullDepth_NegativeInfinityLastChosen_LandsOnFloor()
    {
        await Assert.That(AnchorRadiusHeuristic.Suggest(null, double.NegativeInfinity))
            .IsEqualTo(AnchorRadiusHeuristic.MinSuggestedMeters);
    }

    [Test]
    public async Task NullDepth_LastChosenAboveCeiling_KeepsValue()
    {
        // The fallback path intentionally does NOT clamp at
        // MaxSuggestedMeters: a helm who's chosen 250 m for a
        // genuinely-deep anchorage shouldn't have it silently
        // capped at 200 m the next time they drop. Pin the
        // behaviour so a refactor that adds the clamp surfaces.
        await Assert.That(AnchorRadiusHeuristic.Suggest(null, 250.0)).IsEqualTo(250);
    }

    // --- IsUsableDepth (now public) ----------------------------------
    // Single source of truth shared with DescribeSuggestion + Suggest;
    // pin a few cases so a future tweak is visible to consumers.

    [Test]
    public async Task IsUsableDepth_RejectsNonFiniteAndOutOfRange()
    {
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(double.NaN)).IsFalse();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(double.PositiveInfinity)).IsFalse();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(double.NegativeInfinity)).IsFalse();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(0.0)).IsFalse();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(-1.0)).IsFalse();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(1500.0)).IsFalse();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(0.5)).IsTrue();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(6.0)).IsTrue();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(999.99)).IsTrue();
    }

    // --- DescribeSuggestion (eyebrow label) --------------------------
    // Same gate as Suggest. The exact format ("5x 6.0 m depth" / "from
    // last drop") is consumed by AnchorEditPanel's eyebrow; cross-call
    // invariant tests pin it so a multiplier change can't drift.

    [Test]
    public async Task DescribeSuggestion_UsableDepth_IncludesScopeAndDepth()
    {
        await Assert.That(AnchorRadiusHeuristic.DescribeSuggestion(6.0))
            .IsEqualTo("5x 6.0 m depth");
        await Assert.That(AnchorRadiusHeuristic.DescribeSuggestion(13.5))
            .IsEqualTo("5x 13.5 m depth");
    }

    [Test]
    public async Task DescribeSuggestion_NoDepth_FromLastDrop()
    {
        await Assert.That(AnchorRadiusHeuristic.DescribeSuggestion(null))
            .IsEqualTo("from last drop");
    }

    [Test]
    public async Task DescribeSuggestion_UnusableDepth_FromLastDrop()
    {
        // Same gate as Suggest -- non-finite or out-of-range depth
        // routes to the fallback message so the eyebrow doesn't lie
        // about what the heuristic actually used.
        await Assert.That(AnchorRadiusHeuristic.DescribeSuggestion(double.NaN))
            .IsEqualTo("from last drop");
        await Assert.That(AnchorRadiusHeuristic.DescribeSuggestion(double.PositiveInfinity))
            .IsEqualTo("from last drop");
        await Assert.That(AnchorRadiusHeuristic.DescribeSuggestion(0.0))
            .IsEqualTo("from last drop");
        await Assert.That(AnchorRadiusHeuristic.DescribeSuggestion(-3.0))
            .IsEqualTo("from last drop");
        await Assert.That(AnchorRadiusHeuristic.DescribeSuggestion(1500.0))
            .IsEqualTo("from last drop");
    }

    [Test]
    public async Task ScopeMultiplier_IsWhole_SoIntCastInLabelIsLossless()
    {
        // The eyebrow renders ScopeMultiplier as "(int)5" -> "5".
        // If a future bump to 5.5 lands silently the label would
        // show "5x" while the heuristic uses 5.5x -- a one-line
        // contradiction. Either the multiplier stays whole (this
        // test pins it) or the format string moves to "G3" so
        // the displayed value tracks the applied value.
        double mod = AnchorRadiusHeuristic.ScopeMultiplier % 1.0;
        await Assert.That(mod).IsEqualTo(0.0);
    }

    // --- Property-style continuity check -----------------------------
    // For a sweep across the typical-anchorage depth range, the
    // result must always land in [floor, ceiling]. Catches a
    // refactor that breaks the clamp at a boundary spot-checks
    // happen to miss.

    [Test]
    public async Task Suggest_AcrossDepthRange_AlwaysInClampBounds()
    {
        for (double d = 0.1; d < 60.0; d += 0.3)
        {
            var got = AnchorRadiusHeuristic.Suggest(d, 30.0);
            await Assert.That(got).IsGreaterThanOrEqualTo(AnchorRadiusHeuristic.MinSuggestedMeters);
            await Assert.That(got).IsLessThanOrEqualTo(AnchorRadiusHeuristic.MaxSuggestedMeters);
        }
    }
}
