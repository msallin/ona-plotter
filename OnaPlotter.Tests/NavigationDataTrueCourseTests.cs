using OnaPlotter.Models;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins <see cref="NavigationData.CourseOverGroundTrueResolved"/>, the
/// true-north COG used by CPA / COLREGS geometry. AIS COG is always
/// true, so own-ship COG fed into the same math must be true as well,
/// independent of the helm's magnetic display preference.
/// </summary>
public class NavigationDataTrueCourseTests
{
    private const double Deg = Math.PI / 180.0;

    [Test]
    public async Task TruePublished_WinsEvenWhenHelmPrefersMagnetic()
    {
        var d = new NavigationData { PreferMagneticCourse = true };
        d.Apply("navigation.courseOverGroundTrue", 10 * Deg);
        d.Apply("navigation.courseOverGroundMagnetic", 30 * Deg);
        d.Apply("navigation.magneticVariation", -20 * Deg);

        await Assert.That(d.CourseOverGround!.Value).IsEqualTo(30 * Deg).Within(1e-12);
        await Assert.That(d.CourseOverGroundTrueResolved!.Value).IsEqualTo(10 * Deg).Within(1e-12);
    }

    [Test]
    public async Task MagneticOnly_WithVariation_CorrectsToTrue()
    {
        // Variation is east-positive: true = magnetic + variation.
        var d = new NavigationData();
        d.Apply("navigation.courseOverGroundMagnetic", 100 * Deg);
        d.Apply("navigation.magneticVariation", 5 * Deg);

        await Assert.That(d.MagneticVariation!.Value).IsEqualTo(5 * Deg).Within(1e-12);
        await Assert.That(d.CourseOverGroundTrueResolved!.Value).IsEqualTo(105 * Deg).Within(1e-12);
    }

    [Test]
    [Arguments(355.0, 10.0, 5.0)]
    [Arguments(5.0, -10.0, 355.0)]
    [Arguments(0.0, 0.0, 0.0)]
    public async Task MagneticOnly_WrapsIntoZeroToTwoPi(double magDeg, double varDeg, double expectedDeg)
    {
        var d = new NavigationData();
        d.Apply("navigation.courseOverGroundMagnetic", magDeg * Deg);
        d.Apply("navigation.magneticVariation", varDeg * Deg);

        await Assert.That(d.CourseOverGroundTrueResolved!.Value).IsEqualTo(expectedDeg * Deg).Within(1e-9);
    }

    [Test]
    public async Task MagneticOnly_NoVariation_FallsBackToMagnetic()
    {
        // Last resort: off by the local variation, but a course beats no
        // CPA at all on a boat whose GPS only publishes magnetic.
        var d = new NavigationData();
        d.Apply("navigation.courseOverGroundMagnetic", 42 * Deg);

        await Assert.That(d.CourseOverGroundTrueResolved!.Value).IsEqualTo(42 * Deg).Within(1e-12);
    }

    [Test]
    public async Task NoCourse_IsNull()
    {
        var d = new NavigationData();
        d.Apply("navigation.magneticVariation", 3 * Deg);
        await Assert.That(d.CourseOverGroundTrueResolved).IsNull();
    }

    [Test]
    public async Task NonFiniteVariation_IsIgnored()
    {
        var d = new NavigationData();
        d.Apply("navigation.courseOverGroundMagnetic", 42 * Deg);
        d.Apply("navigation.magneticVariation", double.NaN);
        await Assert.That(d.CourseOverGroundTrueResolved!.Value).IsEqualTo(42 * Deg).Within(1e-12);
    }
}
