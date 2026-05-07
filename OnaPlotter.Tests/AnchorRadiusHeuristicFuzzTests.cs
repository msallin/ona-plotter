using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Property-based fuzz tests for <see cref="AnchorRadiusHeuristic.Suggest"/>.
/// The result drives the anchor-drop modal's pre-filled radius; a regression
/// that returns a value outside [20, 200] would silently configure the alarm
/// at a tighter or looser ring than the rule says.
///
/// <para>The helper has thoughtful guards (NaN, +/-Infinity, comments call
/// out int.MinValue/MaxValue cast behaviour). These fuzz cases pin the
/// contract so a future refactor that drops one of those guards fails
/// here loud.</para>
/// </summary>
public class AnchorRadiusHeuristicFuzzTests
{
    // 1000 iterations cover the (depth, last) input space densely enough
    // that the seed-pinned random walk hits every guarded code path
    // (NaN, infinity, int.MinValue/MaxValue, depth-driven vs fallback).
    // The previous 5000 was a holdover from a debugging session and
    // dominated the suite duration without buying additional coverage.
    private const int Iterations = 1000;
    private const int Seed = 0x4E_C8_05_42;

    [Test]
    public async Task Suggest_AlwaysAtLeastFloor()
    {
        // Floor is enforced on every path. The ceiling is enforced only
        // when the depth-driven branch fires (see the next test for that
        // half of the contract); the fallback path intentionally
        // preserves a helm's stored "I really want 250 m for this deep
        // anchorage" value - pinned in the
        // NullDepth_LastChosenAboveCeiling_KeepsValue regression test.
        var rng = new Random(Seed);
        for (int i = 0; i < Iterations; i++)
        {
            double? depth = i % 7 == 0 ? null : SometimesWeird(rng, 1000);
            double last = SometimesWeird(rng, 5000);
            int r = AnchorRadiusHeuristic.Suggest(depth, last);
            await Assert.That(r)
                .IsGreaterThanOrEqualTo(AnchorRadiusHeuristic.MinSuggestedMeters);
        }
    }

    [Test]
    public async Task Suggest_DepthDriven_AlwaysWithinClampedRange()
    {
        // When the depth-driven branch fires, result is clamped to
        // [MinSuggestedMeters, MaxSuggestedMeters]. Fuzz with depths
        // that exercise the edge cases (just above 0, just below 1000,
        // around the 5x = 200 transition).
        var rng = new Random(Seed ^ 8);
        for (int i = 0; i < 1000; i++)
        {
            // 0.001..999.99 - always a usable depth.
            double depth = 0.001 + rng.NextDouble() * 999.0;
            double last = rng.NextDouble() * 100.0;     // last value irrelevant when depth fires
            int r = AnchorRadiusHeuristic.Suggest(depth, last);
            await Assert.That(r)
                .IsGreaterThanOrEqualTo(AnchorRadiusHeuristic.MinSuggestedMeters);
            await Assert.That(r)
                .IsLessThanOrEqualTo(AnchorRadiusHeuristic.MaxSuggestedMeters);
        }
    }

    [Test]
    public async Task Suggest_DepthDriven_Equals5xDepthClamped()
    {
        // When depth is usable (positive, finite, < 1000m), the rule is
        // 5x scope rounded to whole metres, clamped to [20, 200]. Pin
        // against random in-range depths so a regression that flipped
        // ScopeMultiplier or rounded down to 4x silently shifts every
        // helm's anchor radius.
        var rng = new Random(Seed ^ 1);
        for (int i = 0; i < 1000; i++)
        {
            // 0.1..900 m - well inside the IsUsableDepth range.
            double depth = 0.1 + rng.NextDouble() * 899.0;
            int got = AnchorRadiusHeuristic.Suggest(depth, lastChosenMeters: 0);
            int expected = Math.Clamp(
                (int)Math.Round(depth * AnchorRadiusHeuristic.ScopeMultiplier),
                AnchorRadiusHeuristic.MinSuggestedMeters,
                AnchorRadiusHeuristic.MaxSuggestedMeters);
            await Assert.That(got).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Suggest_NaNDepthFallsBackToLastOrFloor()
    {
        // NaN depth -> use last; weird last (NaN/Infinity) -> floor.
        // Pin both arms.
        await Assert.That(AnchorRadiusHeuristic.Suggest(double.NaN, 50))
            .IsEqualTo(50);
        await Assert.That(AnchorRadiusHeuristic.Suggest(double.NaN, double.NaN))
            .IsEqualTo(AnchorRadiusHeuristic.MinSuggestedMeters);
        await Assert.That(AnchorRadiusHeuristic.Suggest(double.NaN, double.PositiveInfinity))
            .IsEqualTo(AnchorRadiusHeuristic.MaxSuggestedMeters);
        await Assert.That(AnchorRadiusHeuristic.Suggest(double.NaN, double.NegativeInfinity))
            .IsEqualTo(AnchorRadiusHeuristic.MinSuggestedMeters);
    }

    [Test]
    public async Task Suggest_DepthOutOfRange_FallsThroughToLast()
    {
        // Negative, zero, or >= 1000m depth is unusable -> last wins.
        await Assert.That(AnchorRadiusHeuristic.Suggest(-1.0, 80))
            .IsEqualTo(80);
        await Assert.That(AnchorRadiusHeuristic.Suggest(0.0, 80))
            .IsEqualTo(80);
        await Assert.That(AnchorRadiusHeuristic.Suggest(1500.0, 80))
            .IsEqualTo(80);
        await Assert.That(AnchorRadiusHeuristic.Suggest(double.PositiveInfinity, 80))
            .IsEqualTo(80);
    }

    [Test]
    public async Task DescribeSuggestion_ContainsDepthWhenUsable()
    {
        // The "5x N.N m depth" label is the matching eyebrow. Pin the
        // shape so a tweak (e.g. switching to "x" symbol or omitting
        // the depth) is caught by the parity contract.
        var rng = new Random(Seed ^ 2);
        for (int i = 0; i < 100; i++)
        {
            double depth = 0.1 + rng.NextDouble() * 899.0;
            string label = AnchorRadiusHeuristic.DescribeSuggestion(depth);
            await Assert.That(label).Contains("5x");
            await Assert.That(label).Contains("m depth");
        }
    }

    [Test]
    public async Task DescribeSuggestion_FallbackLabelWhenDepthMissing()
    {
        // No depth -> "from last drop". Pin the wording - the modal's
        // eyebrow text reads from this string verbatim.
        await Assert.That(AnchorRadiusHeuristic.DescribeSuggestion(null))
            .IsEqualTo("from last drop");
        await Assert.That(AnchorRadiusHeuristic.DescribeSuggestion(double.NaN))
            .IsEqualTo("from last drop");
        await Assert.That(AnchorRadiusHeuristic.DescribeSuggestion(-3.0))
            .IsEqualTo("from last drop");
        await Assert.That(AnchorRadiusHeuristic.DescribeSuggestion(2000.0))
            .IsEqualTo("from last drop");
    }

    [Test]
    public async Task IsUsableDepth_AcceptsOnlyPositiveFiniteUnder1000()
    {
        // Pin the public predicate so the shared gate between Suggest
        // and DescribeSuggestion stays consistent. A regression that
        // accepts negative depth would silently put the eyebrow into
        // disagreement with the radius (eyebrow says "from last drop",
        // radius computed from a negative depth).
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(0.1)).IsTrue();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(50.0)).IsTrue();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(999.99)).IsTrue();

        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(0.0)).IsFalse();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(-1.0)).IsFalse();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(1000.0)).IsFalse();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(double.NaN)).IsFalse();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(double.PositiveInfinity)).IsFalse();
        await Assert.That(AnchorRadiusHeuristic.IsUsableDepth(double.NegativeInfinity)).IsFalse();
    }

    /// <summary>Random number that's mostly normal but occasionally NaN
    /// / +/-Infinity / huge / negative, so fuzz hits every guard.</summary>
    private static double SometimesWeird(Random rng, double baseScale)
    {
        int dice = rng.Next(0, 100);
        if (dice < 80) return rng.NextDouble() * baseScale;
        if (dice < 85) return double.NaN;
        if (dice < 88) return double.PositiveInfinity;
        if (dice < 91) return double.NegativeInfinity;
        if (dice < 94) return -rng.NextDouble() * baseScale;
        if (dice < 97) return rng.NextDouble() * 1e15;             // wildly large
        return 0.0;
    }
}
