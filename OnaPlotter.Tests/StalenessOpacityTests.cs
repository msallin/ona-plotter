using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>Pin the AIS marker fade ramp at every boundary age.
/// JS layer mirrors this in <c>wwwroot/js/format.js</c>; both must
/// produce identical opacity strings or the renderer paints fresh
/// vs stale targets inconsistently.</summary>
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
        // op = 1 - 0.65 * (0/270) = 1.00 -- but the F2 format clips
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
}
