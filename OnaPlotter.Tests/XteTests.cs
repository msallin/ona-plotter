using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class XteTests
{
    [Test]
    public async Task OnLine_WhenNull()
    {
        await Assert.That(Xte.Classify(null)).IsEqualTo(Xte.Severity.OnLine);
    }

    [Test]
    public async Task OnLine_WhenNaNOrInfinity()
    {
        await Assert.That(Xte.Classify(double.NaN)).IsEqualTo(Xte.Severity.OnLine);
        await Assert.That(Xte.Classify(double.PositiveInfinity)).IsEqualTo(Xte.Severity.OnLine);
        await Assert.That(Xte.Classify(double.NegativeInfinity)).IsEqualTo(Xte.Severity.OnLine);
    }

    [Test]
    public async Task OnLine_AtZero()
    {
        await Assert.That(Xte.Classify(0)).IsEqualTo(Xte.Severity.OnLine);
    }

    [Test]
    public async Task OnLine_BelowOnLineThreshold()
    {
        await Assert.That(Xte.Classify(49.0)).IsEqualTo(Xte.Severity.OnLine);
        await Assert.That(Xte.Classify(-49.0)).IsEqualTo(Xte.Severity.OnLine);
    }

    [Test]
    public async Task Drifting_AtAndAboveOnLineThreshold()
    {
        // 50 m boundary: not strictly less than 50 -> drifting.
        await Assert.That(Xte.Classify(50.0)).IsEqualTo(Xte.Severity.Drifting);
        await Assert.That(Xte.Classify(-50.0)).IsEqualTo(Xte.Severity.Drifting);
        await Assert.That(Xte.Classify(150.0)).IsEqualTo(Xte.Severity.Drifting);
    }

    [Test]
    public async Task OffCourse_AtAndAboveOffCourseThreshold()
    {
        // 200 m boundary: not strictly less than 200 -> off course.
        await Assert.That(Xte.Classify(200.0)).IsEqualTo(Xte.Severity.OffCourse);
        await Assert.That(Xte.Classify(-200.0)).IsEqualTo(Xte.Severity.OffCourse);
        await Assert.That(Xte.Classify(500.0)).IsEqualTo(Xte.Severity.OffCourse);
    }

    [Test]
    public async Task SymmetricInSign()
    {
        // Sign should not change the band - the legend's bands are
        // symmetric around the leg.
        await Assert.That(Xte.Classify(75.0)).IsEqualTo(Xte.Classify(-75.0));
        await Assert.That(Xte.Classify(250.0)).IsEqualTo(Xte.Classify(-250.0));
    }
}
