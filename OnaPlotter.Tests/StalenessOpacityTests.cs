using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>Pin the AIS marker fade ramp at every boundary age, for
/// both the default and a helm-configured faded threshold. The JS
/// renderer applies the string verbatim.</summary>
public class StalenessOpacityTests
{
    [Test]
    public async Task Compute_Fresh_ReturnsNullSoCallerClearsInline()
    {
        // 0..30 s old -> caller should clear inline opacity so the
        // CSS default applies. Returning null is the explicit signal.
        await Assert.That(StalenessOpacity.Compute(0)).IsNull();
        await Assert.That(StalenessOpacity.Compute(15)).IsNull();
        await Assert.That(StalenessOpacity.Compute(29.999)).IsNull();
    }

    [Test]
    public async Task Compute_AtFreshThreshold_StartsFade()
    {
        // 30 s -> first stale step. Linear formula at age=30:
        // op = 1 - 0.65 * (0/270) = 1.00 - but the F2 format clips
        // trailing zeros, "1.00" stays "1.00".
        await Assert.That(StalenessOpacity.Compute(30)).IsEqualTo("1.00");
    }

    [Test]
    public async Task Compute_Midway_LinearInterpolated()
    {
        // 165 s = exactly halfway in the 30..300 window.
        // op = 1 - 0.65 * (135/270) = 1 - 0.325 = 0.675 -> "0.67" or "0.68"?
        // F2 with banker's: 0.675 typically rounds to 0.68 in .NET (away-from-zero default differs).
        // We don't pin the exact intermediate; just check it's between fresh and stale.
        var s = StalenessOpacity.Compute(165);
        await Assert.That(s).IsNotNull();
        var op = double.Parse(s!, System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(op).IsGreaterThan(0.5);
        await Assert.That(op).IsLessThan(0.8);
    }

    [Test]
    public async Task Compute_NearStaleEnd_NearFloor()
    {
        // 299 s old -> just shy of the floor.
        var s = StalenessOpacity.Compute(299);
        await Assert.That(s).IsNotNull();
        var op = double.Parse(s!, System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(op).IsLessThan(0.4);
        await Assert.That(op).IsGreaterThan(0.34);
    }

    [Test]
    public async Task Compute_AtStaleThreshold_PinnedAtFloor()
    {
        // 300+ s -> hard pin at 0.25 (the "essentially gone" floor).
        await Assert.That(StalenessOpacity.Compute(300)).IsEqualTo("0.25");
        await Assert.That(StalenessOpacity.Compute(600)).IsEqualTo("0.25");
        await Assert.That(StalenessOpacity.Compute(86400)).IsEqualTo("0.25");
    }

    [Test]
    public async Task Compute_CustomThreshold_PinsFloorAtThreshold()
    {
        // Harbour helm set "AIS inactive" to 2 min: the floor arrives
        // at 120 s instead of 300 s.
        await Assert.That(StalenessOpacity.Compute(119, fadedSeconds: 120)).IsNotEqualTo("0.25");
        await Assert.That(StalenessOpacity.Compute(120, fadedSeconds: 120)).IsEqualTo("0.25");
        await Assert.That(StalenessOpacity.Compute(200, fadedSeconds: 120)).IsEqualTo("0.25");
    }

    [Test]
    public async Task Compute_CustomThreshold_RampSpansFreshToThreshold()
    {
        // 75 s is halfway through a 30..120 window: 1 - 0.65 * 0.5.
        var op = double.Parse(StalenessOpacity.Compute(75, fadedSeconds: 120)!,
            System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(op).IsEqualTo(0.675).Within(0.006);
    }

    [Test]
    public async Task Compute_LongThreshold_StaysAboveFloorPastDefault()
    {
        // 15 min threshold: a 300 s target is still on the ramp.
        var op = double.Parse(StalenessOpacity.Compute(300, fadedSeconds: 900)!,
            System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(op).IsGreaterThan(0.25);
        await Assert.That(op).IsLessThan(1.0);
    }

    [Test]
    [Arguments(0.0)]
    [Arguments(30.0)]
    [Arguments(-5.0)]
    [Arguments(double.NaN)]
    public async Task Compute_DegenerateThreshold_PinsAtFloorPastFresh(double faded)
    {
        // A threshold at or below the fresh edge has no ramp: fresh
        // targets stay fresh, anything older pins at the floor.
        await Assert.That(StalenessOpacity.Compute(10, faded)).IsNull();
        await Assert.That(StalenessOpacity.Compute(31, faded)).IsEqualTo("0.25");
    }

    [Test]
    public async Task Compute_JustPastFresh_WithDegenerateThreshold_StillOnRamp()
    {
        // Fallback window is 30..31 s, so exactly 30 s is the ramp top.
        await Assert.That(StalenessOpacity.Compute(30, 0)).IsEqualTo("1.00");
    }
}
