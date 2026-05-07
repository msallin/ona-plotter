using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class RouteEtaTests
{
    // Fixed clock used across all tests so the HH:mm portion is
    // deterministic. Without this, "60 s ttg renders ETA = now + 1m"
    // would only pass at exactly the right minute.
    private static readonly DateTime FixedNow =
        new(2026, 4, 28, 14, 30, 0, DateTimeKind.Local);

    // === null / non-finite / non-positive guards ===

    [Test]
    public async Task Format_Null_ReturnsNull()
    {
        await Assert.That(RouteEta.Format(null, FixedNow)).IsNull();
    }

    [Test]
    public async Task Format_NaN_ReturnsNull()
    {
        await Assert.That(RouteEta.Format(double.NaN, FixedNow)).IsNull();
    }

    [Test]
    public async Task Format_PositiveInfinity_ReturnsNull()
    {
        await Assert.That(RouteEta.Format(double.PositiveInfinity, FixedNow)).IsNull();
    }

    [Test]
    public async Task Format_NegativeInfinity_ReturnsNull()
    {
        await Assert.That(RouteEta.Format(double.NegativeInfinity, FixedNow)).IsNull();
    }

    [Test]
    public async Task Format_Zero_ReturnsNull()
    {
        await Assert.That(RouteEta.Format(0, FixedNow)).IsNull();
    }

    [Test]
    public async Task Format_Negative_ReturnsNull()
    {
        await Assert.That(RouteEta.Format(-1, FixedNow)).IsNull();
    }

    // === minute floor ===

    [Test]
    public async Task Format_OneSecond_RoundsUpToOneMinute()
    {
        // Math.Max(1, ...) keeps the line consistent with the
        // populated HH:mm. Rendering "(in 0m)" alongside a real ETA
        // would contradict itself.
        var result = RouteEta.Format(1, FixedNow);
        await Assert.That(result).IsEqualTo("ETA 14:30 (in 1m)");
    }

    [Test]
    public async Task Format_FifteenSeconds_RoundsToZeroMinutesButFloors()
    {
        // 15 s rounds to 0 min via Math.Round; the Max(1, ...) clamp
        // bumps to 1m. Same behaviour as the JS twin.
        var result = RouteEta.Format(15, FixedNow);
        await Assert.That(result).IsEqualTo("ETA 14:30 (in 1m)");
    }

    [Test]
    public async Task Format_45Seconds_RoundsTo1m()
    {
        var result = RouteEta.Format(45, FixedNow);
        await Assert.That(result).IsEqualTo("ETA 14:30 (in 1m)");
    }

    // === sub-hour formatting ===

    [Test]
    public async Task Format_OneMinute_RendersAsMinutes()
    {
        var result = RouteEta.Format(60, FixedNow);
        await Assert.That(result).IsEqualTo("ETA 14:31 (in 1m)");
    }

    [Test]
    public async Task Format_30Minutes_RendersAsMinutes()
    {
        var result = RouteEta.Format(30 * 60, FixedNow);
        await Assert.That(result).IsEqualTo("ETA 15:00 (in 30m)");
    }

    [Test]
    public async Task Format_59Minutes_RendersAsMinutes()
    {
        var result = RouteEta.Format(59 * 60, FixedNow);
        await Assert.That(result).IsEqualTo("ETA 15:29 (in 59m)");
    }

    // === hour formatting ===

    [Test]
    public async Task Format_OneHour_RendersAsHoursMinutes()
    {
        var result = RouteEta.Format(3600, FixedNow);
        await Assert.That(result).IsEqualTo("ETA 15:30 (in 1h 0m)");
    }

    [Test]
    public async Task Format_OneHourTwelveMinutes_RendersComponents()
    {
        // FixedNow = 14:30; +1h12m = 15:42
        var result = RouteEta.Format(3600 + 12 * 60, FixedNow);
        await Assert.That(result).IsEqualTo("ETA 15:42 (in 1h 12m)");
    }

    [Test]
    public async Task Format_TwoHoursThirtyOneMinutes_RendersComponents()
    {
        // FixedNow = 14:30; +2h31m = 17:01
        var result = RouteEta.Format(2 * 3600 + 31 * 60, FixedNow);
        await Assert.That(result).IsEqualTo("ETA 17:01 (in 2h 31m)");
    }

    [Test]
    public async Task Format_99HoursExactly_RendersAtCap()
    {
        var result = RouteEta.Format(99.0 * 3600, FixedNow);
        // 99h * 3600 = 356400 s; 356400/60 = 5940 min; 5940 / 60 = 99h
        // 99 is at-but-not-past the cap, so the standard "Xh Ym"
        // path renders.
        await Assert.That(result).StartsWith("ETA ");
        await Assert.That(result).Contains("(in 99h ");
    }

    // === >99h overflow ===

    [Test]
    public async Task Format_Above99Hours_RendersWith99Cap()
    {
        // 100h ttg should render "(in >99h)" rather than the truncated
        // "99h 59m" the previous JS twin produced (which lied about
        // its own arrival time - the wall-clock ETA at +100h would
        // be ~4 days from now).
        var result = RouteEta.Format(100.0 * 3600, FixedNow);
        await Assert.That(result).Contains("(in >99h)");
    }

    [Test]
    public async Task Format_300Hours_RendersWith99Cap()
    {
        var result = RouteEta.Format(300.0 * 3600, FixedNow);
        await Assert.That(result).Contains("(in >99h)");
    }

    // === clock seam pin ===

    [Test]
    public async Task Format_DifferentClock_ProducesDifferentEta()
    {
        // Same ttg, different "now" -> different HH:mm. Pins that
        // the clock parameter actually drives the wall-clock side
        // of the output (regression guard for an accidental
        // DateTime.Now reintroduction).
        var morning = new DateTime(2026, 4, 28, 6, 0, 0, DateTimeKind.Local);
        var afternoon = new DateTime(2026, 4, 28, 18, 0, 0, DateTimeKind.Local);
        var ttg = 3600.0;

        await Assert.That(RouteEta.Format(ttg, morning))
            .IsEqualTo("ETA 07:00 (in 1h 0m)");
        await Assert.That(RouteEta.Format(ttg, afternoon))
            .IsEqualTo("ETA 19:00 (in 1h 0m)");
    }

    [Test]
    public async Task Format_RollsOverMidnight()
    {
        // ttg that crosses midnight should render the next-day HH:mm
        // (DateTime.AddSeconds handles the rollover; no special
        // logic in Format).
        var nearMidnight = new DateTime(2026, 4, 28, 23, 50, 0, DateTimeKind.Local);
        var result = RouteEta.Format(20 * 60, nearMidnight);
        await Assert.That(result).IsEqualTo("ETA 00:10 (in 20m)");
    }
}
