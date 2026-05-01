using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the "anchor TWD, rotate after lookback, fire if shift exceeds
/// threshold" behaviour of WIND SHIFT. The rule is stateful (_anchorDeg,
/// _anchorAt) so every test builds a fresh instance and drives it with
/// explicit timestamps through the evaluation context.
/// </summary>
public class WindShiftAlarmRuleTests
{
    private const double DegToRad = Math.PI / 180.0;
    private const double KnotsToMs = 1.0 / 1.94384;

    private static NavigationData NavWithTwd(double twdDeg, double? twsKn = null)
    {
        var nav = new NavigationData();
        nav.Apply("environment.wind.directionTrue", twdDeg * DegToRad);
        if (twsKn is double kn)
            nav.Apply("environment.wind.speedTrue", kn * KnotsToMs);
        return nav;
    }

    // Default minTws=0 keeps the existing tests gate-free; tests that
    // exercise the gate set it explicitly.
    private static AlarmEvaluationContext Ctx(NavigationData nav, DateTime now,
        double threshold = 15, double lookback = 5, double minTws = 0)
        => new(nav, [],
            new FakeSettings
            {
                WindShiftAlarmThreshold = threshold,
                WindShiftLookbackMinutes = lookback,
                WindShiftMinTrueWindSpeed = minTws,
            },
            now, _ => false);

    [Test]
    public async Task No_TWD_No_Alarm()
    {
        var rule = new WindShiftAlarmRule();
        var nav = new NavigationData();
        await Assert.That(rule.Check(Ctx(nav, DateTime.UtcNow))).IsNull();
    }

    [Test]
    public async Task First_Sample_Anchors_But_Does_Not_Fire()
    {
        // The rule needs TWO samples spanning the lookback window to
        // decide whether a shift happened. The first call must always
        // return null (anchor only) regardless of the wind value.
        var rule = new WindShiftAlarmRule();
        var start = DateTime.UtcNow;
        await Assert.That(rule.Check(Ctx(NavWithTwd(90), start))).IsNull();
    }

    [Test]
    public async Task Within_Lookback_Silent()
    {
        // Anchor at 90deg, 2 min later wind is at 140deg. 5-min lookback
        // hasn't elapsed, so even a big shift stays silent -- the rule
        // won't rotate the anchor until the window passes.
        var rule = new WindShiftAlarmRule();
        var t0 = DateTime.UtcNow;
        rule.Check(Ctx(NavWithTwd(90), t0));
        await Assert.That(rule.Check(Ctx(NavWithTwd(140), t0.AddMinutes(2)))).IsNull();
    }

    [Test]
    public async Task Shift_Beyond_Threshold_Fires_After_Lookback()
    {
        // Anchor 90deg, 5 min later 120deg -> 30deg shift, threshold 15.
        // Should fire, and the message embeds the shift magnitude so a
        // glance at the banner tells the user HOW MUCH the wind moved.
        var rule = new WindShiftAlarmRule();
        var t0 = DateTime.UtcNow;
        rule.Check(Ctx(NavWithTwd(90), t0));
        var alarm = rule.Check(Ctx(NavWithTwd(120), t0.AddMinutes(5)));

        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Title).IsEqualTo("WIND SHIFT");
        await Assert.That(alarm.Severity).IsEqualTo(AlarmSeverity.Warn);
        await Assert.That(alarm.Message).Contains("30");
    }

    [Test]
    public async Task Shift_Below_Threshold_Does_Not_Fire()
    {
        var rule = new WindShiftAlarmRule();
        var t0 = DateTime.UtcNow;
        rule.Check(Ctx(NavWithTwd(90), t0));
        // 10deg shift, threshold 15 -- no alarm, but the anchor rotates.
        await Assert.That(rule.Check(Ctx(NavWithTwd(100), t0.AddMinutes(5)))).IsNull();
    }

    [Test]
    public async Task Shift_Across_North_Wraps_Correctly()
    {
        // 350 -> 10 is a 20deg shift, not 340. The rule's modular
        // arithmetic keeps circular direction correct.
        var rule = new WindShiftAlarmRule();
        var t0 = DateTime.UtcNow;
        rule.Check(Ctx(NavWithTwd(350), t0));
        var alarm = rule.Check(Ctx(NavWithTwd(10), t0.AddMinutes(5), threshold: 15));

        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Message).Contains("20");
    }

    [Test]
    public async Task Anchor_Rotates_On_Fire_So_Subsequent_Samples_Re_Arm()
    {
        // After a shift fires, the rule re-anchors at the new TWD so the
        // NEXT lookback window compares against the post-shift direction.
        var rule = new WindShiftAlarmRule();
        var t0 = DateTime.UtcNow;
        rule.Check(Ctx(NavWithTwd(0), t0));
        var first = rule.Check(Ctx(NavWithTwd(40), t0.AddMinutes(5)));
        await Assert.That(first).IsNotNull();

        // Next 5 min: TWD stable at 40 -- 0deg shift against the new
        // anchor, no alarm.
        await Assert.That(rule.Check(Ctx(NavWithTwd(40), t0.AddMinutes(10)))).IsNull();
    }

    [Test]
    public async Task Latched_AutoClear_False()
    {
        // WIND SHIFT is a transient "event" notification, not a
        // continuously-true condition. Latching keeps the banner up
        // until the user dismisses; matches the pre-refactor behaviour.
        await Assert.That(new WindShiftAlarmRule().AutoClear).IsFalse();
    }

    [Test]
    public async Task Custom_Threshold_And_Lookback_Honoured()
    {
        // 50deg shift but threshold 60 -- no alarm even though lookback
        // has elapsed.
        var rule = new WindShiftAlarmRule();
        var t0 = DateTime.UtcNow;
        rule.Check(Ctx(NavWithTwd(0), t0, threshold: 60, lookback: 2));
        await Assert.That(rule.Check(Ctx(NavWithTwd(50), t0.AddMinutes(2), threshold: 60, lookback: 2))).IsNull();
    }

    [Test]
    public async Task Below_Min_Tws_Suppresses_Alarm_Even_With_Big_Shift()
    {
        // 60 deg shift across 5 min, but TWS is 2 kn against a 3 kn
        // gate -- the rule should never arm. In light air the TWD
        // computation is dominated by heading / SOG noise and a 60 deg
        // swing means nothing.
        var rule = new WindShiftAlarmRule();
        var t0 = DateTime.UtcNow;
        rule.Check(Ctx(NavWithTwd(90, twsKn: 2.0), t0, minTws: 3));
        var alarm = rule.Check(Ctx(NavWithTwd(150, twsKn: 2.0), t0.AddMinutes(5), minTws: 3));
        await Assert.That(alarm).IsNull();
    }

    [Test]
    public async Task Above_Min_Tws_Behaves_Normally()
    {
        // Same shift, but TWS 8 kn -- well above the 3 kn gate. The
        // alarm should fire as it would without the gate.
        var rule = new WindShiftAlarmRule();
        var t0 = DateTime.UtcNow;
        rule.Check(Ctx(NavWithTwd(90, twsKn: 8.0), t0, minTws: 3));
        var alarm = rule.Check(Ctx(NavWithTwd(120, twsKn: 8.0), t0.AddMinutes(5), minTws: 3));
        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Message).Contains("30");
    }

    [Test]
    public async Task Missing_Tws_Path_Bypasses_Gate()
    {
        // Server doesn't publish environment.wind.speedTrue -- the gate
        // must not silently suppress every shift on those installs. The
        // rule falls back to its un-gated behaviour.
        var rule = new WindShiftAlarmRule();
        var t0 = DateTime.UtcNow;
        rule.Check(Ctx(NavWithTwd(90), t0, minTws: 3));
        var alarm = rule.Check(Ctx(NavWithTwd(120), t0.AddMinutes(5), minTws: 3));
        await Assert.That(alarm).IsNotNull();
    }

    [Test]
    public async Task Null_Tws_After_Live_Suppresses_Like_LightWind()
    {
        // signalk-derived-data publishes TWS=null on ticks where it
        // can't derive the value (SOG missing / zero). Once the rule
        // has seen TWS go live, a subsequent null is functionally
        // identical to "below threshold": the wind data is unreliable
        // and the gate is supposed to suppress on unreliable data.
        // Pin both: the alarm is suppressed AND the anchor resets so
        // when real wind returns the rule re-anchors fresh.
        var rule = new WindShiftAlarmRule();
        var t0 = DateTime.UtcNow;
        // Tick 1: TWS=8 kn live; rule anchors at 90 deg.
        rule.Check(Ctx(NavWithTwd(90, twsKn: 8.0), t0, minTws: 3));
        // Tick 2: TWS goes null (SK plugin can't derive); TWD jumps
        // 60 deg from heading noise. Without the fix this would fire
        // a "60 deg shift" alarm; with the fix it suppresses.
        var alarm = rule.Check(Ctx(NavWithTwd(150), t0.AddMinutes(5), minTws: 3));
        await Assert.That(alarm).IsNull();

        // Tick 3: real wind returns at 8 kn, but the anchor was
        // dropped on the null tick, so this is a fresh first-armed
        // sample -- no alarm against the pre-null anchor.
        var rearmed = rule.Check(Ctx(NavWithTwd(140, twsKn: 8.0), t0.AddMinutes(10), minTws: 3));
        await Assert.That(rearmed).IsNull();
    }

    [Test]
    public async Task Null_Tws_Without_Prior_Live_Bypasses_Gate()
    {
        // Same scenario as Missing_Tws_Path_Bypasses_Gate but with
        // an explicit null on the second tick instead of just absent.
        // The rule has never seen TWS go live, so the null reads as
        // "this server doesn't publish TWS" rather than "becalmed";
        // the bypass keeps installs without TWS publishing from
        // losing every wind-shift alarm under a >0 minTws setting.
        var rule = new WindShiftAlarmRule();
        var t0 = DateTime.UtcNow;
        rule.Check(Ctx(NavWithTwd(90), t0, minTws: 3));
        var alarm = rule.Check(Ctx(NavWithTwd(120), t0.AddMinutes(5), minTws: 3));
        await Assert.That(alarm).IsNotNull();
    }

    [Test]
    public async Task Min_Tws_Zero_Disables_Gate_Even_With_Tws_Reading()
    {
        // minTws = 0 is the explicit "off switch": the rule arms
        // regardless of how light the wind is. Helm who wants every
        // shift can set the gate to 0 and get pre-gate behaviour.
        var rule = new WindShiftAlarmRule();
        var t0 = DateTime.UtcNow;
        rule.Check(Ctx(NavWithTwd(90, twsKn: 0.5), t0, minTws: 0));
        var alarm = rule.Check(Ctx(NavWithTwd(120, twsKn: 0.5), t0.AddMinutes(5), minTws: 0));
        await Assert.That(alarm).IsNotNull();
    }

    [Test]
    public async Task Becalmed_Period_Drops_Anchor_Instead_Of_Reporting_Stale_Shift()
    {
        // Anchor at 90 deg in 8 kn, then wind dies (1 kn) for an hour --
        // boat lies to current and TWD drifts to 270. When real wind
        // returns at 280 deg, we must NOT report a "190 deg shift in 5
        // min" against the pre-becalmed anchor. The gate drops the
        // anchor while wind is below threshold so the next live wind
        // re-anchors fresh.
        var rule = new WindShiftAlarmRule();
        var t0 = DateTime.UtcNow;
        rule.Check(Ctx(NavWithTwd(90, twsKn: 8.0), t0, minTws: 3));
        // 30 minutes of light air: anchor should be cleared.
        rule.Check(Ctx(NavWithTwd(180, twsKn: 1.0), t0.AddMinutes(10), minTws: 3));
        rule.Check(Ctx(NavWithTwd(270, twsKn: 1.0), t0.AddMinutes(30), minTws: 3));
        // Wind comes back: this is the FIRST armed sample post-becalm,
        // so it just re-anchors -- no alarm.
        var first = rule.Check(Ctx(NavWithTwd(280, twsKn: 8.0), t0.AddMinutes(40), minTws: 3));
        await Assert.That(first).IsNull();
        // 5 min later, wind steady at 285 -- 5 deg shift against the
        // post-becalm anchor, well below threshold, no alarm.
        var second = rule.Check(Ctx(NavWithTwd(285, twsKn: 8.0), t0.AddMinutes(45), minTws: 3));
        await Assert.That(second).IsNull();
    }
}
