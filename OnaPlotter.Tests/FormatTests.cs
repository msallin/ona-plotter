using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class FormatTests
{
    [Test]
    public async Task Speed_Null_ReturnsDashes()
    {
        await Assert.That(Format.Speed(null)).IsEqualTo("--");
    }

    [Test]
    public async Task Speed_MsToKnots_OneDecimal()
    {
        // 5 m/s ~= 9.7 knots
        await Assert.That(Format.Speed(5.0)).IsEqualTo("9.7");
    }

    [Test]
    public async Task Degrees_Null_ReturnsDashes()
    {
        await Assert.That(Format.Degrees(null)).IsEqualTo("--");
    }

    [Test]
    public async Task Degrees_ConvertsAndWrapsNegative()
    {
        // -PI/2 rad = -90 deg -> wraps to 270
        await Assert.That(Format.Degrees(-Math.PI / 2)).IsEqualTo("270");
    }

    [Test]
    public async Task Degrees_RoundsToInteger()
    {
        await Assert.That(Format.Degrees(Math.PI)).IsEqualTo("180");
    }

    [Test]
    public async Task Nm_Under10_TwoDecimals()
    {
        // 1852m = 1.00 nm
        await Assert.That(Format.Nm(1852.0)).IsEqualTo("1.00");
    }

    [Test]
    public async Task Nm_Over10_OneDecimal()
    {
        // 20 nm = 37040 m
        await Assert.That(Format.Nm(37040.0)).IsEqualTo("20.0");
    }

    [Test]
    public async Task MetersDual_Under1000_Meters()
    {
        await Assert.That(Format.MetersDual(500.0)).IsEqualTo("500m");
    }

    [Test]
    public async Task MetersDual_Over1000_Nm()
    {
        // 1852m = 1.00nm
        await Assert.That(Format.MetersDual(1852.0)).IsEqualTo("1.00nm");
    }

    [Test]
    public async Task Depth_OneDecimal()
    {
        await Assert.That(Format.Depth(12.345)).IsEqualTo("12.3");
    }

    [Test]
    public async Task TimeToGo_UnderHour_MinutesSeconds()
    {
        // 5m30s = 330s
        await Assert.That(Format.TimeToGo(330)).IsEqualTo("5m30s");
    }

    [Test]
    public async Task TimeToGo_OverHour_HoursMinutes()
    {
        // 1h 30m 45s = 5445s
        await Assert.That(Format.TimeToGo(5445)).IsEqualTo("1h30m");
    }

    [Test]
    public async Task Xte_Starboard_HasSSuffix()
    {
        // 100m to starboard
        await Assert.That(Format.Xte(100.0)).IsEqualTo("100m S");
    }

    [Test]
    public async Task Xte_Port_HasPSuffix()
    {
        await Assert.That(Format.Xte(-50.0)).IsEqualTo("50m P");
    }

    [Test]
    public async Task Xte_Large_Nm()
    {
        // 1852m = 1.00nm
        await Assert.That(Format.Xte(1852.0)).IsEqualTo("1.00nm S");
    }

    [Test]
    public async Task XteClass_Under50_Ok()
    {
        await Assert.That(Format.XteClass(30)).IsEqualTo("xte-ok");
    }

    [Test]
    public async Task XteClass_50to200_Warn()
    {
        await Assert.That(Format.XteClass(-100)).IsEqualTo("xte-warn");
    }

    [Test]
    public async Task XteClass_Over200_Danger()
    {
        await Assert.That(Format.XteClass(500)).IsEqualTo("xte-danger");
    }

    [Test]
    public async Task DepthClass_Under3_Danger()
    {
        await Assert.That(Format.DepthClass(2.5)).IsEqualTo("depth-danger");
    }

    [Test]
    public async Task DepthClass_3to8_Warn()
    {
        await Assert.That(Format.DepthClass(5)).IsEqualTo("depth-warn");
    }

    [Test]
    public async Task DepthClass_Over8_Ok()
    {
        await Assert.That(Format.DepthClass(20)).IsEqualTo("depth-ok");
    }
}
