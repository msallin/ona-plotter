using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

public class AnchorTideAlarmRuleTests
{
    // IAppSettings stub lives in OnaPlotter.Tests/FakeSettings.cs -- shared
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
        var settings = new FakeSettings { BoatDraftMeters = 0.8, AnchorTideSafetyMargin = 1.0 };
        var nav = BuildNav(anchored: true, depth: 3.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(2));

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
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(4));

        var alarm = rule.Check(Ctx(nav, new FakeSettings(), now));

        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Severity).IsEqualTo(AlarmSeverity.Danger);
        await Assert.That(alarm.Message).Contains("touches");
    }

    [Test]
    public async Task SignalKDraft_Preferred_Over_SettingsDraft()
    {
        // FakeSettings.BoatDraftMeters defaults to 1.5; the SK vessel
        // has design.draft.current = 2.0. Depth 2.3, tide drops 0.5
        // -> LW depth 1.8. Using Settings draft 1.5 -> clearance 0.3
        // (Warn if margin is >0.3). Using SK draft 2.0 -> clearance
        // -0.2 (Danger: keel touches). So if SK is preferred, alarm
        // is Danger; if Settings wins, it's at most Warn. This pins
        // the preference explicitly.
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.3,
            heightNow: 2.0, heightLow: 1.5, timeLow: now.AddHours(3),
            signalkDraft: 2.0);

        var alarm = rule.Check(Ctx(nav, new FakeSettings { BoatDraftMeters = 1.5 }, now));

        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Severity).IsEqualTo(AlarmSeverity.Danger);
    }

    [Test]
    public async Task SettingsDraft_UsedWhen_SignalKDraft_Missing()
    {
        // No SK-published draft -> fall back to manual Settings value.
        // Same numeric scenario as above but SK doesn't publish; manual
        // is 1.5, so clearance 0.3 hits Warn (default margin 1.0 > 0.3).
        var rule = new AnchorTideAlarmRule();
        var now = DateTime.UtcNow;
        var nav = BuildNav(anchored: true, depth: 2.3,
            heightNow: 2.0, heightLow: 1.5, timeLow: now.AddHours(3));

        var alarm = rule.Check(Ctx(nav, new FakeSettings { BoatDraftMeters = 1.5 }, now));

        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Severity).IsEqualTo(AlarmSeverity.Warn);
    }

    [Test]
    public async Task RisingTide_NoAlarm()
    {
        // heightNow < heightLow means the next "low water" is actually
        // higher than current height -- i.e. tide is rising into its
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
        // LW is 10 hours away -- beyond the 6h lookahead. Don't alarm
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
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(4));

        var first = rule.Check(Ctx(nav, new FakeSettings(), now));
        await Assert.That(first).IsNotNull();

        rule.OnDismissed(first!, now);

        // Same conditions, minutes later -- still silent.
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
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(4));

        var first = rule.Check(Ctx(nav, settings, now));
        await Assert.That(first).IsNotNull();
        rule.OnDismissed(first!, now);

        // Anchor goes up -- the tick with AnchorActive=false resets the latch.
        var up = BuildNav(anchored: false);
        await Assert.That(rule.Check(Ctx(up, settings, now.AddMinutes(1)))).IsNull();

        // Re-anchor with the same grounding prediction. Alarm is back.
        var again = BuildNav(anchored: true, depth: 2.0,
            heightNow: 2.0, heightLow: 0.5, timeLow: now.AddHours(4));
        var second = rule.Check(Ctx(again, settings, now.AddMinutes(2)));
        await Assert.That(second).IsNotNull();
    }
}
