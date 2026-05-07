using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

public class AnchorTideAlarmRuleTests
{
    // IAppSettings stub lives in OnaPlotter.Tests/FakeSettings.cs - shared
    // across every alarm-rule test class. Tests that need non-default
    // values use object-initialiser syntax on the mutable properties.

    private static NavigationData BuildNav(
        bool anchored,
        double? depth = null,
        double? heightNow = null,
        double? heightLow = null,
        DateTime? timeLow = null,
        double? signalkDraft = null)
    {
        var nav = new NavigationData();
        if (anchored) nav.ApplyAnchorPosition(47.4, 8.5);
        if (depth is not null) nav.Apply("environment.depth.belowTransducer", depth);
        if (heightNow is not null) nav.Apply("environment.tide.heightNow", heightNow);
        if (heightLow is not null) nav.Apply("environment.tide.heightLow", heightLow);
        if (timeLow is not null)
            nav.ApplyString("environment.tide.timeLow",
                timeLow.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        if (signalkDraft is not null) nav.Apply("design.draft.current", signalkDraft);
        return nav;
    }

    private static AlarmEvaluationContext Ctx(NavigationData nav, IAppSettings settings, DateTime now)
        => new(nav, [], settings, now, _ => false);

    [Test]
    public async Task NotAnchored_NoAlarm()
    {
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: false, depth: 3.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(3));
        await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
    }

    [Test]
    public async Task Anchored_NoTideData_NoAlarm()
    {
        // Without tide data, the rule can't predict LW and goes silent.
        // This is the default state for servers without a tide plugin.
        var rule = new AnchorTideAlarmRule();
        var nav = BuildNav(anchored: true, depth: 3.0);
        await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), DateTime.UtcNow))).IsNull();
    }

    [Test]
    public async Task Anchored_SafeClearanceAtLw_NoAlarm()
    {
        // 8m depth now, tide drops 2m -> 6m at LW. Draft 1.5m, margin 1m.
        // Clearance 4.5m >> 1m margin. No alarm.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 8.0,
            heightNow: 2.5, heightLow: 0.5, timeLow: now.AddHours(3));
        await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
    }

    [Test]
    public async Task Anchored_ThinClearance_Warns()
    {
        // 3m depth now, tide drops 1.5m -> 1.5m at LW. Draft 0.8m, margin 1m.
        // Clearance 0.7m < margin 1m but > 0 -> Warn (not Danger).
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var settings = new FakeSettings { AnchorTideSafetyMargin = 1.0 };
        var nav = BuildNav(anchored: true, depth: 3.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(2),
            signalkDraft: 0.8);

        var alarm = rule.Check(Ctx(nav, settings, now));

        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Title).IsEqualTo("ANCHOR TIDE");
        await Assert.That(alarm.Severity).IsEqualTo(AlarmSeverity.Warn);
        await Assert.That(alarm.Message).Contains("clearance");
    }

    [Test]
    public async Task Anchored_GroundingExpected_Dangers()
    {
        // 2m depth now, tide drops 1.5m -> 0.5m at LW. Draft 1.5m.
        // Clearance -1m (keel below bottom by 1m) -> Danger.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(4),
            signalkDraft: 1.5);

        var alarm = rule.Check(Ctx(nav, new FakeSettings(), now));

        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Severity).IsEqualTo(AlarmSeverity.Danger);
        await Assert.That(alarm.Message).Contains("touches");
    }

    [Test]
    public async Task NoSignalKDraft_NoAlarm()
    {
        // Draft comes from SignalK only. Without design.draft.current
        // (or .maximum) the rule stays dormant - better a quiet alarm
        // than one running on a guessed default that could mask a real
        // grounding risk. The dormancy hint in the HUD tells the user
        // to set vessel.json.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.3,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(3));
        // NB: no signalkDraft passed.
        await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
    }

    [Test]
    public async Task RisingTide_NoAlarm()
    {
        // heightNow < heightLow means the next "low water" is actually
        // higher than current height - i.e. tide is rising into its
        // next low or we're already past it. No grounding risk.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.0,
            heightNow: 0.4, heightLow: 0.5, timeLow: now.AddHours(2));
        await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
    }

    [Test]
    public async Task LwTooFarInFuture_NoAlarm()
    {
        // LW is 10 hours away - beyond the 6h lookahead. Don't alarm
        // on things the user has time to wake up for.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(10));
        await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
    }

    [Test]
    public async Task LwInPast_NoAlarm()
    {
        // Stale tide data (timeLow already passed) shouldn't alarm.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(-1));
        await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
    }

    [Test]
    public async Task Dismiss_SilencesUntilAnchorUp()
    {
        // After dismissal the rule must not re-fire for the same anchored
        // session, even if the predicted grounding is still imminent. The
        // manager's 30s cooldown is too short for a multi-hour prediction.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(4),
            signalkDraft: 1.5);

        var first = rule.Check(Ctx(nav, new FakeSettings(), now));
        await Assert.That(first).IsNotNull();

        rule.OnDismissed(first!, now);

        // Same conditions, minutes later - still silent.
        var later = rule.Check(Ctx(nav, new FakeSettings(), now.AddMinutes(5)));
        await Assert.That(later).IsNull();
    }

    [Test]
    public async Task Dismiss_ResetsOnAnchorUp()
    {
        // Lift anchor and re-drop: the next anchoring starts fresh and
        // the rule is free to warn again.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var settings = new FakeSettings();
        var nav = BuildNav(anchored: true, depth: 2.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(4),
            signalkDraft: 1.5);

        var first = rule.Check(Ctx(nav, settings, now));
        await Assert.That(first).IsNotNull();
        rule.OnDismissed(first!, now);

        // Anchor goes up - the tick with AnchorActive=false resets the latch.
        var up = BuildNav(anchored: false);
        await Assert.That(rule.Check(Ctx(up, settings, now.AddMinutes(1)))).IsNull();

        // Re-anchor with the same grounding prediction. Alarm is back.
        var again = BuildNav(anchored: true, depth: 2.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(4),
            signalkDraft: 1.5);
        var second = rule.Check(Ctx(again, settings, now.AddMinutes(2)));
        await Assert.That(second).IsNotNull();
    }

    // --- Missing-data prerequisites: each "no X, no alarm" branch ---

    [Test]
    public async Task Anchored_NoDepth_NoAlarm()
    {
        // Boundary: depth from below-transducer never showed up. Without
        // a depth reading the rule can't subtract the predicted drop, so
        // it must stay quiet - doing the math against null would mean
        // emitting a phantom warn off zero clearance.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(2),
            signalkDraft: 1.5);
        // Note: no depth.
        await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
    }

    [Test]
    public async Task Anchored_NoTideHeightNow_NoAlarm()
    {
        // Tide plugin half-published: heightLow + timeLow but no heightNow.
        // Without the current value we can't compute the drop.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 3.0,
            heightLow: 0.5, timeLow: now.AddHours(2),
            signalkDraft: 1.5);
        await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
    }

    [Test]
    public async Task Anchored_NoTideHeightLow_NoAlarm()
    {
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 3.0,
            heightNow: 2.0, timeLow: now.AddHours(2),
            signalkDraft: 1.5);
        await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
    }

    [Test]
    public async Task Anchored_NoTideTimeLow_NoAlarm()
    {
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 3.0,
            heightNow: 2.0, heightLow: 0.5,
            signalkDraft: 1.5);
        await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
    }

    // --- Time-formatting branch: hours vs minutes-only display ---

    [Test]
    public async Task Anchored_LowWaterUnderOneHour_MessageShowsMinutesOnly()
    {
        // When LW is < 1 hour away, the message reads "Xmin" (no hours
        // segment) so the helm sees the urgency clearly. The two
        // formatting branches matter because a copy-paste regression
        // could format both identically and lose the punchy "23 min"
        // form.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddMinutes(35),
            signalkDraft: 1.5);

        var alarm = rule.Check(Ctx(nav, new FakeSettings(), now));
        await Assert.That(alarm).IsNotNull();
        // Format is "{mm}min" (no leading h0) - pin the absence of "h"
        // so a refactor that always prints "0h35" doesn't ship.
        await Assert.That(alarm!.Message).Contains("min");
        await Assert.That(alarm.Message).DoesNotContain("0h");
    }

    [Test]
    public async Task Anchored_LowWaterMultipleHours_MessageShowsHoursAndMinutes()
    {
        // The hours-and-minutes branch: at 2h35m the message reads
        // "2h35" so the helm can compare against "do I have time to
        // sleep?". Pin both sides of the formatter.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddMinutes(155),  // 2h35
            signalkDraft: 1.5);

        var alarm = rule.Check(Ctx(nav, new FakeSettings(), now));
        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Message).Contains("2h");
    }

    [Test]
    public async Task ExactlyAtMargin_StaysSilent()
    {
        // Boundary: clearance exactly equals margin -> alarm is silent
        // (uses >=). A refactor that flipped to a strict > would
        // suddenly start nagging at the safe-margin boundary.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var settings = new FakeSettings { AnchorTideSafetyMargin = 1.0 };
        // 3m depth, 1m drop -> 2m at LW. 1m draft -> 1m clearance == margin.
        var nav = BuildNav(anchored: true, depth: 3.0,
            heightNow: 2.0, heightLow: 1.0, timeLow: now.AddHours(2),
            signalkDraft: 1.0);
        await Assert.That(rule.Check(Ctx(nav, settings, now))).IsNull();
    }
}
