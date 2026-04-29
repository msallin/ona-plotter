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
        // The analyzer thinks comparing constants is pointless --
        // it is, at compile time, but the assertion still runs at
        // test time, which is precisely the drift signal we want.
#pragma warning disable TUnitAssertions0005
        await Assert.That(ChartUpscale.MinLevels).IsEqualTo(0);
        await Assert.That(ChartUpscale.MaxLevels).IsEqualTo(3);
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
    public async Task ClampLevels_AboveCeiling_ClampsToThree()
    {
        await Assert.That(ChartUpscale.ClampLevels(4)).IsEqualTo(3);
        await Assert.That(ChartUpscale.ClampLevels(99)).IsEqualTo(3);
    }

    [Test]
    public async Task ClampLevels_InRange_KeepsValue()
    {
        await Assert.That(ChartUpscale.ClampLevels(0)).IsEqualTo(0);
        await Assert.That(ChartUpscale.ClampLevels(1)).IsEqualTo(1);
        await Assert.That(ChartUpscale.ClampLevels(2)).IsEqualTo(2);
        await Assert.That(ChartUpscale.ClampLevels(3)).IsEqualTo(3);
    }

    [Test]
    public async Task Effective_DisabledMaster_ReturnsZero()
    {
        // Even a configured value of 3 must be ignored when the master
        // flag is off -- the call site uses Effective to collapse the
        // two settings into a single integer the JS decorator can use.
        await Assert.That(ChartUpscale.Effective(false, 3)).IsEqualTo(0);
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
    }

    [Test]
    public async Task Effective_EnabledMaster_ClampsOutOfRange()
    {
        await Assert.That(ChartUpscale.Effective(true, -1)).IsEqualTo(0);
        await Assert.That(ChartUpscale.Effective(true, 99)).IsEqualTo(3);
    }
}
