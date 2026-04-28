using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class WeatherOpacityTests
{
    [Test]
    public async Task PercentAndFractionConstants_AgreeAtBoundaries()
    {
        // Behavioural pin: the integer min / max constants must
        // round-trip through PercentToFraction to the fraction
        // floor / ceiling. If any of MinFraction / MaxFraction /
        // MinPercent / MaxPercent drifts in isolation, this fails.
        // Done via PercentToFraction (a runtime call) rather than
        // direct constant compare so TUnit doesn't flag a constant-
        // vs-constant tautology.
        await Assert.That(WeatherOpacity.PercentToFraction(WeatherOpacity.MinPercent))
            .IsEqualTo(WeatherOpacity.MinFraction);
        await Assert.That(WeatherOpacity.PercentToFraction(WeatherOpacity.MaxPercent))
            .IsEqualTo(WeatherOpacity.MaxFraction);
    }

    [Test]
    public async Task ClampFraction_BelowFloor_ClampsUp()
    {
        await Assert.That(WeatherOpacity.ClampFraction(0)).IsEqualTo(0.05);
        await Assert.That(WeatherOpacity.ClampFraction(-0.5)).IsEqualTo(0.05);
        await Assert.That(WeatherOpacity.ClampFraction(0.04)).IsEqualTo(0.05);
    }

    [Test]
    public async Task ClampFraction_AboveCeiling_ClampsDown()
    {
        await Assert.That(WeatherOpacity.ClampFraction(1.0)).IsEqualTo(0.95);
        await Assert.That(WeatherOpacity.ClampFraction(2.5)).IsEqualTo(0.95);
        await Assert.That(WeatherOpacity.ClampFraction(0.96)).IsEqualTo(0.95);
    }

    [Test]
    public async Task ClampFraction_InRange_KeepsValue()
    {
        await Assert.That(WeatherOpacity.ClampFraction(0.5)).IsEqualTo(0.5);
        await Assert.That(WeatherOpacity.ClampFraction(0.05)).IsEqualTo(0.05);
        await Assert.That(WeatherOpacity.ClampFraction(0.95)).IsEqualTo(0.95);
    }

    [Test]
    public async Task ClampPercent_BelowFloor_ClampsUp()
    {
        await Assert.That(WeatherOpacity.ClampPercent(0)).IsEqualTo(5);
        await Assert.That(WeatherOpacity.ClampPercent(-50)).IsEqualTo(5);
        await Assert.That(WeatherOpacity.ClampPercent(4)).IsEqualTo(5);
    }

    [Test]
    public async Task ClampPercent_AboveCeiling_ClampsDown()
    {
        await Assert.That(WeatherOpacity.ClampPercent(100)).IsEqualTo(95);
        await Assert.That(WeatherOpacity.ClampPercent(200)).IsEqualTo(95);
        await Assert.That(WeatherOpacity.ClampPercent(96)).IsEqualTo(95);
    }

    [Test]
    public async Task PercentToFraction_RoundTrips()
    {
        await Assert.That(WeatherOpacity.PercentToFraction(50)).IsEqualTo(0.5);
        await Assert.That(WeatherOpacity.PercentToFraction(5)).IsEqualTo(0.05);
        await Assert.That(WeatherOpacity.PercentToFraction(95)).IsEqualTo(0.95);
    }

    [Test]
    public async Task PercentToFraction_ClampsBeforeDividing()
    {
        // A caller that passes 0 must NOT produce 0.0; the percent
        // gets clamped to 5 first and divides to 0.05.
        await Assert.That(WeatherOpacity.PercentToFraction(0)).IsEqualTo(0.05);
        await Assert.That(WeatherOpacity.PercentToFraction(-100)).IsEqualTo(0.05);
        await Assert.That(WeatherOpacity.PercentToFraction(150)).IsEqualTo(0.95);
    }
}
