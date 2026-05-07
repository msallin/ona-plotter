using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the shallow-depth alarm's rearm behaviour. The rule fires
/// when depth is below the threshold; auto-clears when depth rises
/// back above it. After an explicit user dismissal, it must NOT
/// re-fire until depth has been sustained above threshold for
/// <see cref="ShallowAlarmRule.RearmClearDuration"/> (5 minutes).
/// A boat rolling in swell on a shoal would otherwise retrigger the
/// alarm every few seconds even after the helmsman acked.
/// </summary>
public class ShallowAlarmRuleTests
{
    private static NavigationData NavAtDepth(double depthMeters)
    {
        var nav = new NavigationData();
        nav.Apply("environment.depth.belowTransducer", depthMeters);
        return nav;
    }

    private static AlarmEvaluationContext Ctx(NavigationData nav, double threshold, DateTime now)
        => new(nav, [], new FakeSettings { DepthAlarmThreshold = threshold }, now, _ => false);

    [Test]
    public async Task Fires_When_Below_Threshold()
    {
        var rule = new ShallowAlarmRule();
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var alarm = rule.Check(Ctx(NavAtDepth(1.5), 3.0, now));
        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Title).IsEqualTo("SHALLOW");
        await Assert.That(alarm.Severity).IsEqualTo(AlarmSeverity.Danger);
    }

    [Test]
    public async Task Silent_When_Above_Threshold()
    {
        var rule = new ShallowAlarmRule();
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await Assert.That(rule.Check(Ctx(NavAtDepth(5.0), 3.0, now))).IsNull();
    }

    [Test]
    public async Task After_Dismiss_Silent_While_Still_Shallow()
    {
        // Typical "swell on a shoal" scenario: depth dips back under
        // threshold right after dismiss. Rule must stay quiet.
        var rule = new ShallowAlarmRule();
        var t0 = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var firstAlarm = rule.Check(Ctx(NavAtDepth(1.5), 3.0, t0));
        await Assert.That(firstAlarm).IsNotNull();

        rule.OnDismissed(firstAlarm!, t0);

        // Same shallow depth on the next tick - rule is muted.
        await Assert.That(rule.Check(Ctx(NavAtDepth(1.5), 3.0, t0.AddSeconds(1)))).IsNull();
        await Assert.That(rule.Check(Ctx(NavAtDepth(2.0), 3.0, t0.AddSeconds(30)))).IsNull();
    }

    [Test]
    public async Task After_Dismiss_Silent_While_Still_Clear_But_Under_Rearm_Window()
    {
        // Helm acked, depth cleared, but we're still within the 5 min
        // rearm window. No alarm even if depth dips briefly below
        // threshold mid-window.
        var rule = new ShallowAlarmRule();
        var t0 = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var alarm = rule.Check(Ctx(NavAtDepth(1.5), 3.0, t0));
        rule.OnDismissed(alarm!, t0);

        // Clear at +1 min - rule stays quiet.
        await Assert.That(rule.Check(Ctx(NavAtDepth(4.0), 3.0, t0.AddMinutes(1)))).IsNull();

        // Brief dip back under threshold at +2 min should restart the
        // sustained-clear timer - still quiet.
        await Assert.That(rule.Check(Ctx(NavAtDepth(2.5), 3.0, t0.AddMinutes(2)))).IsNull();

        // Back clear at +3 min, still under the 5 min gate from NOW
        // (measuring from the most-recent clear tick, since the dip
        // reset the timer).
        await Assert.That(rule.Check(Ctx(NavAtDepth(4.0), 3.0, t0.AddMinutes(3)))).IsNull();
    }

    [Test]
    public async Task Rearms_After_Sustained_Clear_Then_Fires_On_New_Shallow()
    {
        // Full cycle: shallow -> dismiss -> depth clears for 5 min ->
        // depth drops again -> alarm must fire.
        var rule = new ShallowAlarmRule();
        var t0 = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var first = rule.Check(Ctx(NavAtDepth(1.5), 3.0, t0));
        rule.OnDismissed(first!, t0);

        // Depth above threshold continuously for 5 min.
        await Assert.That(rule.Check(Ctx(NavAtDepth(5.0), 3.0, t0.AddMinutes(1)))).IsNull();
        await Assert.That(rule.Check(Ctx(NavAtDepth(5.0), 3.0, t0.AddMinutes(3)))).IsNull();
        await Assert.That(rule.Check(Ctx(NavAtDepth(5.0), 3.0, t0.AddMinutes(5)))).IsNull();

        // Just past 5 min, still clear - rearm has occurred; rule is
        // silent because depth is still above threshold.
        await Assert.That(rule.Check(Ctx(NavAtDepth(5.0), 3.0, t0.AddMinutes(6)))).IsNull();

        // Now we dip back into shallow again - this must re-alarm.
        var reArmed = rule.Check(Ctx(NavAtDepth(1.0), 3.0, t0.AddMinutes(7)));
        await Assert.That(reArmed).IsNotNull();
        await Assert.That(reArmed!.Title).IsEqualTo("SHALLOW");
    }

    [Test]
    public async Task Null_Depth_After_Dismiss_Does_Not_Start_Rearm_Clock()
    {
        // Instrument dropped out mid-dismiss. Null depth is "unknown",
        // not "clear" - we must NOT start the 5 min clock until we
        // have a real reading above threshold.
        var rule = new ShallowAlarmRule();
        var t0 = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var alarm = rule.Check(Ctx(NavAtDepth(1.5), 3.0, t0));
        rule.OnDismissed(alarm!, t0);

        var nav = new NavigationData(); // no depth field set
        await Assert.That(rule.Check(Ctx(nav, 3.0, t0.AddMinutes(10)))).IsNull();

        // Even after "10 minutes of no data" + a new shallow reading,
        // the rule should re-arm because the previous null does not
        // count as a clear tick (rearm requires sustained clear).
        // Check: shallow again immediately -> still suppressed because
        // we never had a sustained clear.
        await Assert.That(rule.Check(Ctx(NavAtDepth(1.2), 3.0, t0.AddMinutes(10)))).IsNull();
    }

    // --- GetRearmStatus: the chip surface for the post-dismiss UI ---

    [Test]
    public async Task GetRearmStatus_Null_BeforeAnyDismissal()
    {
        // Boundary: a freshly constructed rule (or one whose alarm has
        // never been dismissed) must report "no rearm in progress" so
        // the UI doesn't render a phantom countdown chip on first paint.
        var rule = new ShallowAlarmRule();
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await Assert.That(rule.GetRearmStatus(now)).IsNull();
    }

    [Test]
    public async Task GetRearmStatus_AfterDismiss_StillShallow_ReportsIndefinite()
    {
        // After ack while still on the shoal, the chip should explain
        // "waiting for clear depth" with SecondsRemaining=0 so the UI
        // renders the indefinite-wait variant rather than a numeric
        // countdown that would tick from 5:00 every render and confuse
        // the helm into thinking the alarm is about to come back.
        var rule = new ShallowAlarmRule();
        var t0 = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var alarm = rule.Check(Ctx(NavAtDepth(1.5), 3.0, t0));
        rule.OnDismissed(alarm!, t0);

        // Still shallow on the next tick - no sustained-clear timer.
        rule.Check(Ctx(NavAtDepth(1.4), 3.0, t0.AddSeconds(1)));

        var status = rule.GetRearmStatus(t0.AddSeconds(2));
        await Assert.That(status).IsNotNull();
        await Assert.That(status!.Value.Title).IsEqualTo("SHALLOW");
        await Assert.That(status.Value.SecondsRemaining).IsEqualTo(0);
        await Assert.That(status.Value.Hint).IsEqualTo("waiting for clear depth");
    }

    [Test]
    public async Task GetRearmStatus_DuringClearCountdown_ReportsRemainingSeconds()
    {
        // Helm acked, depth has cleared, and we're partway through the
        // 5 min sustained-clear gate. The chip must show the countdown
        // so the helm can see "alarm re-arms in N seconds" rather than
        // a static label. Sized so a refactor to ms-precision arithmetic
        // would still pass the tolerance.
        var rule = new ShallowAlarmRule();
        var t0 = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var alarm = rule.Check(Ctx(NavAtDepth(1.5), 3.0, t0));
        rule.OnDismissed(alarm!, t0);

        // Clear from t0+1 min - that becomes _sustainedClearFrom.
        rule.Check(Ctx(NavAtDepth(5.0), 3.0, t0.AddMinutes(1)));

        // Probe 2 min later: 5 min total - 2 min elapsed = 3 min remaining.
        var status = rule.GetRearmStatus(t0.AddMinutes(3));
        await Assert.That(status).IsNotNull();
        await Assert.That(status!.Value.Title).IsEqualTo("SHALLOW");
        await Assert.That(status.Value.Hint).IsEqualTo("clear-depth countdown");
        // 3 min = 180 s; allow 0.5 s tolerance for floating-point drift.
        await Assert.That(status.Value.SecondsRemaining).IsGreaterThan(179.0);
        await Assert.That(status.Value.SecondsRemaining).IsLessThan(181.0);
    }

    [Test]
    public async Task GetRearmStatus_AtRearmBoundary_ReportsAboutToArm()
    {
        // Edge-case: the wall clock has already crossed the 5 min gate
        // but Check hasn't yet run to flip _dismissedAt back to null.
        // The status method must not return a negative SecondsRemaining
        // (which would render as "-2s" on the chip) - it should report
        // 0 and the "about to arm" hint so the chip can fade out
        // gracefully on the next render.
        var rule = new ShallowAlarmRule();
        var t0 = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var alarm = rule.Check(Ctx(NavAtDepth(1.5), 3.0, t0));
        rule.OnDismissed(alarm!, t0);
        rule.Check(Ctx(NavAtDepth(5.0), 3.0, t0.AddMinutes(1)));   // clear timer starts

        // Probe past the 5 min gate (clear@1min + 6min = t0+7min, gate is 5).
        var status = rule.GetRearmStatus(t0.AddMinutes(7));
        await Assert.That(status).IsNotNull();
        await Assert.That(status!.Value.SecondsRemaining).IsEqualTo(0);
        await Assert.That(status.Value.Hint).IsEqualTo("about to arm");
    }

    [Test]
    public async Task GetRearmStatus_Null_AfterFullyRearmed()
    {
        // After Check() has run past the gate and reset _dismissedAt to
        // null, the status must report null again so the chip disappears.
        // This pins the symmetry with GetRearmStatus_Null_BeforeAnyDismissal.
        var rule = new ShallowAlarmRule();
        var t0 = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var alarm = rule.Check(Ctx(NavAtDepth(1.5), 3.0, t0));
        rule.OnDismissed(alarm!, t0);
        // Sustained clear past the gate -> Check() resets state.
        rule.Check(Ctx(NavAtDepth(5.0), 3.0, t0.AddMinutes(1)));
        rule.Check(Ctx(NavAtDepth(5.0), 3.0, t0.AddMinutes(7)));

        await Assert.That(rule.GetRearmStatus(t0.AddMinutes(8))).IsNull();
    }
}
