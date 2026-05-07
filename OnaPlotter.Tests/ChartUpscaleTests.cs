using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the chart-upscale (overzoom) helper. Removable contract:
/// if the feature gets ripped out, this test class deletes alongside
/// <see cref="ChartUpscale"/> + the JS decorator + the Settings flag.
/// </summary>
public class ChartUpscaleTests
{
    [Test]
    public async Task Constants_HoldExpectedValues()
    {
        // Pinned because the Settings UI <input min/max>, the JS
        // decorator clamp, and the persistence loader all reference
        // these values; drift breaks the contract across surfaces.
        // The analyzer thinks comparing constants is pointless -
        // it is, at compile time, but the assertion still runs at
        // test time, which is precisely the drift signal we want.
#pragma warning disable TUnitAssertions0005
        await Assert.That(ChartUpscale.MinLevels).IsEqualTo(0);
        await Assert.That(ChartUpscale.MaxLevels).IsEqualTo(5);
        await Assert.That(ChartUpscale.DefaultLevels).IsEqualTo(2);
#pragma warning restore TUnitAssertions0005
    }

    [Test]
    public async Task ClampLevels_BelowFloor_ClampsToZero()
    {
        await Assert.That(ChartUpscale.ClampLevels(-1)).IsEqualTo(0);
        await Assert.That(ChartUpscale.ClampLevels(-100)).IsEqualTo(0);
    }

    [Test]
    public async Task ClampLevels_AboveCeiling_ClampsToMax()
    {
        await Assert.That(ChartUpscale.ClampLevels(6)).IsEqualTo(5);
        await Assert.That(ChartUpscale.ClampLevels(99)).IsEqualTo(5);
    }

    [Test]
    public async Task ClampLevels_InRange_KeepsValue()
    {
        await Assert.That(ChartUpscale.ClampLevels(0)).IsEqualTo(0);
        await Assert.That(ChartUpscale.ClampLevels(1)).IsEqualTo(1);
        await Assert.That(ChartUpscale.ClampLevels(2)).IsEqualTo(2);
        await Assert.That(ChartUpscale.ClampLevels(3)).IsEqualTo(3);
        await Assert.That(ChartUpscale.ClampLevels(4)).IsEqualTo(4);
        await Assert.That(ChartUpscale.ClampLevels(5)).IsEqualTo(5);
    }

    [Test]
    public async Task Effective_DisabledMaster_ReturnsZero()
    {
        // Even a configured value of 5 must be ignored when the master
        // flag is off - the call site uses Effective to collapse the
        // two settings into a single integer the JS decorator can use.
        await Assert.That(ChartUpscale.Effective(false, 5)).IsEqualTo(0);
        await Assert.That(ChartUpscale.Effective(false, 2)).IsEqualTo(0);
        await Assert.That(ChartUpscale.Effective(false, 0)).IsEqualTo(0);
    }

    [Test]
    public async Task Effective_EnabledMaster_PassesClampedLevels()
    {
        await Assert.That(ChartUpscale.Effective(true, 0)).IsEqualTo(0);
        await Assert.That(ChartUpscale.Effective(true, 1)).IsEqualTo(1);
        await Assert.That(ChartUpscale.Effective(true, 2)).IsEqualTo(2);
        await Assert.That(ChartUpscale.Effective(true, 3)).IsEqualTo(3);
        await Assert.That(ChartUpscale.Effective(true, 5)).IsEqualTo(5);
    }

    [Test]
    public async Task Effective_EnabledMaster_ClampsOutOfRange()
    {
        await Assert.That(ChartUpscale.Effective(true, -1)).IsEqualTo(0);
        await Assert.That(ChartUpscale.Effective(true, 99)).IsEqualTo(5);
    }

    [Test]
    public async Task ClampLevels_AlwaysWithinRange_Property()
    {
        // Property: for every int input, ClampLevels returns a value in
        // [MinLevels, MaxLevels]. Catches a regression that flips
        // MinLevels / MaxLevels constants or replaces Math.Clamp with a
        // hand-rolled branch that mishandles a boundary.
        //
        // Mix of seeded random + known-evil values: int.MinValue,
        // int.MaxValue, exact boundaries, off-by-one.
        var rng = new Random(0x1B_AD_F0_0D);
        var corners = new[] {
            int.MinValue, int.MinValue + 1,
            -1, 0, 1, 2, 3, 4,
            ChartUpscale.MinLevels - 1, ChartUpscale.MinLevels, ChartUpscale.MinLevels + 1,
            ChartUpscale.MaxLevels - 1, ChartUpscale.MaxLevels, ChartUpscale.MaxLevels + 1,
            int.MaxValue - 1, int.MaxValue,
        };
        foreach (var v in corners)
        {
            int r = ChartUpscale.ClampLevels(v);
            await Assert.That(r).IsGreaterThanOrEqualTo(ChartUpscale.MinLevels);
            await Assert.That(r).IsLessThanOrEqualTo(ChartUpscale.MaxLevels);
        }
        for (int i = 0; i < 1000; i++)
        {
            int v = rng.Next(int.MinValue, int.MaxValue);
            int r = ChartUpscale.ClampLevels(v);
            await Assert.That(r).IsGreaterThanOrEqualTo(ChartUpscale.MinLevels);
            await Assert.That(r).IsLessThanOrEqualTo(ChartUpscale.MaxLevels);
        }
    }

    [Test]
    public async Task Effective_DisabledAlwaysReturnsZero_Property()
    {
        // Property: master flag off -> always 0, regardless of levels.
        // A regression that ANDs with the levels rather than gating
        // would surface (e.g. Effective(false, 5) returning 5).
        var rng = new Random(0x42_42_42_42);
        var corners = new[] { int.MinValue, -1, 0, 2, 3, int.MaxValue };
        foreach (var v in corners)
        {
            await Assert.That(ChartUpscale.Effective(false, v)).IsEqualTo(0);
        }
        for (int i = 0; i < 500; i++)
        {
            int v = rng.Next(int.MinValue, int.MaxValue);
            await Assert.That(ChartUpscale.Effective(false, v)).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Effective_EnabledIsClampLevels_Property()
    {
        // Property: master flag on -> result equals ClampLevels(levels).
        // A regression that decouples Effective from ClampLevels (e.g.
        // a separate branch that drops the upper clamp) would surface.
        var rng = new Random(0x07_77_77_77);
        for (int i = 0; i < 500; i++)
        {
            int v = rng.Next(int.MinValue, int.MaxValue);
            await Assert.That(ChartUpscale.Effective(true, v))
                .IsEqualTo(ChartUpscale.ClampLevels(v));
        }
    }
}
