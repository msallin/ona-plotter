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

    private static NavigationData NavWithTwd(double twdDeg)
    {
        var nav = new NavigationData();
        nav.Apply("environment.wind.directionTrue", twdDeg * DegToRad);
        return nav;
    }

    private static AlarmEvaluationContext Ctx(NavigationData nav, DateTime now,
        double threshold = 15, double lookback = 5)
        => new(nav, [],
            new FakeSettings
            {
                WindShiftAlarmThreshold = threshold,
                WindShiftLookbackMinutes = lookback,
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
}
