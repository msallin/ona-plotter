using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pin the chart-display CSS filter contract: clamp limits, the
/// identity-shortcut, and the invariant-culture format string the JS
/// side splices into <c>style.filter</c>. Helms tweak the sliders from
/// the Layers panel; the values land in localStorage; this helper runs
/// on every chart-add path. A regression here either disables the
/// filter pipeline silently (identity-shortcut breaks) or hands the
/// browser a malformed filter (locale comma, out-of-range value),
/// either of which the helm reads as "the slider broke the chart".
/// </summary>
public class ChartFilterTests
{
    [Test]
    public async Task ClampContrastSaturation_BelowFloor_ClampsUp()
    {
        await Assert.That(ChartFilter.ClampContrastSaturation(0)).IsEqualTo(50);
        await Assert.That(ChartFilter.ClampContrastSaturation(-100)).IsEqualTo(50);
        await Assert.That(ChartFilter.ClampContrastSaturation(49)).IsEqualTo(50);
    }

    [Test]
    public async Task ClampContrastSaturation_AboveCeiling_ClampsDown()
    {
        await Assert.That(ChartFilter.ClampContrastSaturation(201)).IsEqualTo(200);
        await Assert.That(ChartFilter.ClampContrastSaturation(int.MaxValue)).IsEqualTo(200);
    }

    [Test]
    public async Task ClampContrastSaturation_InRange_KeepsValue()
    {
        await Assert.That(ChartFilter.ClampContrastSaturation(50)).IsEqualTo(50);
        await Assert.That(ChartFilter.ClampContrastSaturation(100)).IsEqualTo(100);
        await Assert.That(ChartFilter.ClampContrastSaturation(130)).IsEqualTo(130);
        await Assert.That(ChartFilter.ClampContrastSaturation(200)).IsEqualTo(200);
    }

    [Test]
    public async Task ClampBrightness_RangePinned()
    {
        await Assert.That(ChartFilter.ClampBrightness(40)).IsEqualTo(50);
        await Assert.That(ChartFilter.ClampBrightness(50)).IsEqualTo(50);
        await Assert.That(ChartFilter.ClampBrightness(120)).IsEqualTo(120);
        await Assert.That(ChartFilter.ClampBrightness(150)).IsEqualTo(150);
        await Assert.That(ChartFilter.ClampBrightness(200)).IsEqualTo(150);
    }

    [Test]
    public async Task IsIdentity_DefaultsTrue()
    {
        await Assert.That(ChartFilter.IsIdentity(100, 100, 100)).IsTrue();
    }

    [Test]
    public async Task IsIdentity_AnyChannelOff_False()
    {
        await Assert.That(ChartFilter.IsIdentity(101, 100, 100)).IsFalse();
        await Assert.That(ChartFilter.IsIdentity(100, 99, 100)).IsFalse();
        await Assert.That(ChartFilter.IsIdentity(100, 100, 110)).IsFalse();
    }

    [Test]
    public async Task Format_AtIdentity_ReturnsEmptyString()
    {
        // Identity short-circuits so the JS side clears style.filter
        // entirely instead of parking an inert "contrast(1)..." string
        // that the compositor still has to evaluate per repaint.
        await Assert.That(ChartFilter.Format(100, 100, 100)).IsEqualTo("");
    }

    [Test]
    public async Task Format_NonIdentity_EmitsCssShorthand()
    {
        await Assert.That(ChartFilter.Format(130, 110, 95))
            .IsEqualTo("contrast(1.30) saturate(1.10) brightness(0.95)");
    }

    [Test]
    public async Task Format_BoundaryValues_RoundTrip()
    {
        await Assert.That(ChartFilter.Format(50, 50, 50))
            .IsEqualTo("contrast(0.50) saturate(0.50) brightness(0.50)");
        await Assert.That(ChartFilter.Format(200, 200, 150))
            .IsEqualTo("contrast(2.00) saturate(2.00) brightness(1.50)");
    }

    [Test]
    public async Task Format_OutOfRangeInputs_ClampedNotPropagated()
    {
        // A bad localStorage value or a JS-bridge bug must not produce
        // "contrast(-3) saturate(50) brightness(0)" (browsers will
        // accept some negatives and still render, but the result is
        // unintelligible). Clamp first, then format.
        await Assert.That(ChartFilter.Format(-5, 9999, 9999))
            .IsEqualTo("contrast(0.50) saturate(2.00) brightness(1.50)");
    }

    [Test]
    public async Task Format_GermanLocale_StillEmitsDotDecimal()
    {
        // Reproduce a regression we've burned on before: a German-
        // locale boot can flip "double".ToString() to use ',' as the
        // decimal separator. CSS rejects "contrast(1,30)" and the
        // entire filter falls off the chart. Force invariant culture
        // by switching the thread + asserting the dot is still there.
        var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var s = ChartFilter.Format(130, 110, 95);
            await Assert.That(s).Contains("1.30");
            await Assert.That(s).DoesNotContain("1,30");
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = prev;
        }
    }
}
