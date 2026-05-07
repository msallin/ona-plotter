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

    // - MobElapsed -----------------------------------------------

    [Test]
    public async Task MobElapsed_NegativeClamps_ToZero()
    {
        // Clock skew or test fixture passing pre-raise instant ->
        // pin a stable display rather than a negative number.
        await Assert.That(Format.MobElapsed(-5)).IsEqualTo("T+0s");
    }

    [Test]
    public async Task MobElapsed_UnderMinute_Seconds()
    {
        await Assert.That(Format.MobElapsed(0)).IsEqualTo("T+0s");
        await Assert.That(Format.MobElapsed(45)).IsEqualTo("T+45s");
        await Assert.That(Format.MobElapsed(59)).IsEqualTo("T+59s");
    }

    [Test]
    public async Task MobElapsed_AtMinute_RollsToMinutes()
    {
        await Assert.That(Format.MobElapsed(60)).IsEqualTo("T+1m");
        await Assert.That(Format.MobElapsed(125)).IsEqualTo("T+2m");
        await Assert.That(Format.MobElapsed(3599)).IsEqualTo("T+59m");
    }

    [Test]
    public async Task MobElapsed_AtHour_RollsToHoursMinutes()
    {
        await Assert.That(Format.MobElapsed(3600)).IsEqualTo("T+1h0m");
        await Assert.That(Format.MobElapsed(3660)).IsEqualTo("T+1h1m");
        await Assert.That(Format.MobElapsed(5025)).IsEqualTo("T+1h23m");
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

    // - RangeRingLabel -------------------------------------------

    [Test]
    public async Task RangeRingLabel_ZeroOrNegative_Empty()
    {
        await Assert.That(Format.RangeRingLabel(0)).IsEqualTo(string.Empty);
        await Assert.That(Format.RangeRingLabel(-1)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task RangeRingLabel_NonFinite_Empty()
    {
        await Assert.That(Format.RangeRingLabel(double.NaN)).IsEqualTo(string.Empty);
        await Assert.That(Format.RangeRingLabel(double.PositiveInfinity)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task RangeRingLabel_Under1Nm_StripsTrailingZeros()
    {
        await Assert.That(Format.RangeRingLabel(0.5)).IsEqualTo("0.5 nm");
        await Assert.That(Format.RangeRingLabel(0.25)).IsEqualTo("0.25 nm");
        await Assert.That(Format.RangeRingLabel(0.1)).IsEqualTo("0.1 nm");
    }

    [Test]
    public async Task RangeRingLabel_OneToTen_StripsDotZero()
    {
        await Assert.That(Format.RangeRingLabel(1.0)).IsEqualTo("1 nm");
        await Assert.That(Format.RangeRingLabel(2.5)).IsEqualTo("2.5 nm");
        // 9.99 rounds up at F1 to "10.0" -> stripped to "10".
        await Assert.That(Format.RangeRingLabel(9.99)).IsEqualTo("10 nm");
    }

    [Test]
    public async Task RangeRingLabel_TenAndAbove_RoundsToInt()
    {
        await Assert.That(Format.RangeRingLabel(10.0)).IsEqualTo("10 nm");
        await Assert.That(Format.RangeRingLabel(50.5)).IsEqualTo("51 nm");
    }

    // EtaWithTtg covered by RouteEta + RouteEtaTests; no duplicate
    // here. The JS mirror in format.js (etaWithTtg) is parity-tested
    // by RouteEtaJsParityTests against the same RouteEta canonical.
}
