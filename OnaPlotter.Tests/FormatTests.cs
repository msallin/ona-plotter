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

    [Test]
    [Arguments(null, false)]
    [Arguments(-0.5, true)]
    [Arguments(0.0, true)]
    [Arguments(2.99, true)]
    [Arguments(7.99, true)]
    [Arguments(8.0, false)]
    [Arguments(120.0, false)]
    public async Task IsShallow_MatchesTheAmberAndRedTints(double? depth, bool expected)
    {
        await Assert.That(Format.IsShallow(depth)).IsEqualTo(expected);
        await Assert.That(Format.IsShallow(depth))
            .IsEqualTo(Format.DepthClass(depth) is "depth-warn" or "depth-danger");
    }

    // - LatLonDms ------------------------------------------------

    [Test]
    public async Task LatLonDms_PositiveLat_PositiveLon_NE()
    {
        await Assert.That(Format.LatLonDms(47.5, 8.5))
            .IsEqualTo("47.50000°N 8.50000°E");
    }

    [Test]
    public async Task LatLonDms_NegativeLat_NegativeLon_SW()
    {
        await Assert.That(Format.LatLonDms(-25.535966, -76.761572))
            .IsEqualTo("25.53597°S 76.76157°W");
    }

    [Test]
    public async Task LatLonDms_FivePrecisionDigits_AlwaysFiveDecimals()
    {
        // Pin five-decimal width so the popup column doesn't jitter
        // when a vessel's last digit changes.
        await Assert.That(Format.LatLonDms(0.0, 0.0))
            .IsEqualTo("0.00000°N 0.00000°E");
    }

    // - CoordDecimal ---------------------------------------------

    [Test]
    public async Task CoordDecimal_NorthEast()
    {
        // Munich, as shown in the route / polygon edit panels.
        await Assert.That(Format.CoordDecimal([48.21444, 11.57532]))
            .IsEqualTo("48.2144° N, 11.5753° E");
    }

    [Test]
    public async Task CoordDecimal_SouthWest_DropsSign()
    {
        await Assert.That(Format.CoordDecimal([-33.85, -151.2]))
            .IsEqualTo("33.8500° S, 151.2000° W");
    }

    [Test]
    public async Task CoordDecimal_ZeroIsNorthEast()
    {
        await Assert.That(Format.CoordDecimal([0, 0]))
            .IsEqualTo("0.0000° N, 0.0000° E");
    }

    [Test]
    public async Task CoordDecimal_ExtraElementsIgnored()
    {
        // GeoJSON-ish triples (lat, lon, altitude) still render.
        await Assert.That(Format.CoordDecimal([10, 20, 99]))
            .IsEqualTo("10.0000° N, 20.0000° E");
    }

    [Test]
    public async Task CoordDecimal_MissingOrShort_ReturnsEmpty()
    {
        await Assert.That(Format.CoordDecimal(null)).IsEqualTo("");
        await Assert.That(Format.CoordDecimal([])).IsEqualTo("");
        await Assert.That(Format.CoordDecimal([47.0])).IsEqualTo("");
    }

    // - RadarRange -----------------------------------------------

    [Test]
    [Arguments(int.MinValue, "-2147483648 m")]
    [Arguments(-1, "-1 m")]
    [Arguments(0, "0 m")]
    [Arguments(1, "1 m")]
    [Arguments(999, "999 m")]
    [Arguments(1000, "0.5 nm")]
    [Arguments(1852, "1.0 nm")]
    [Arguments(74080, "40.0 nm")]
    [Arguments(int.MaxValue, "1159548.4 nm")]
    public async Task RadarRange_MetresBelowOneKmElseNm(int metres, string expected)
    {
        await Assert.That(Format.RadarRange(metres)).IsEqualTo(expected);
    }

    // - DateTimeLocalInput ---------------------------------------

    [Test]
    public async Task DateTimeLocalInput_UsesInputFormatToTheMinute()
    {
        var t = new DateTime(2024, 6, 5, 7, 8, 59, DateTimeKind.Unspecified);
        await Assert.That(Format.DateTimeLocalInput(t)).IsEqualTo("2024-06-05T07:08");
    }

    [Test]
    public async Task DateTimeLocalInput_Boundaries()
    {
        await Assert.That(Format.DateTimeLocalInput(DateTime.MinValue)).IsEqualTo("0001-01-01T00:00");
        await Assert.That(Format.DateTimeLocalInput(DateTime.MaxValue)).IsEqualTo("9999-12-31T23:59");
    }
}
