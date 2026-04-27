using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

/// <summary>
/// Rule-level tests for <see cref="AnchorDragAlarmRule"/>. The anchor
/// drag condition is "currentRadius &gt; maxRadius" with a 2 m dead-band
/// on each side to prevent chatter on gust-driven swings.
/// </summary>
public class AnchorDragAlarmRuleTests
{
    private static NavigationData BuildNav(
        bool anchored, double? maxR = null, double? curR = null)
    {
        var nav = new NavigationData();
        if (anchored) nav.ApplyAnchorPosition(47.4, 8.5);
        if (maxR is not null) nav.Apply("navigation.anchor.maxRadius", maxR);
        if (curR is not null) nav.Apply("navigation.anchor.currentRadius", curR);
        return nav;
    }

    private static AlarmEvaluationContext Ctx(NavigationData nav) =>
        new(nav, [], new FakeSettings(), DateTime.UtcNow, _ => false);

    [Test]
    public async Task NotAnchored_NoAlarm()
    {
        var rule = new AnchorDragAlarmRule();
        var nav = BuildNav(anchored: false, maxR: 30, curR: 40);
        await Assert.That(rule.Check(Ctx(nav))).IsNull();
    }

    [Test]
    public async Task Anchored_InsideRadius_NoAlarm()
    {
        var rule = new AnchorDragAlarmRule();
        var nav = BuildNav(anchored: true, maxR: 30, curR: 25);
        await Assert.That(rule.Check(Ctx(nav))).IsNull();
    }

    [Test]
    public async Task Anchored_JustOverRadius_DoesNotFire_DueToHysteresis()
    {
        // Dead-band is 2 m. 31 m against a 30 m max is inside the
        // upper band; should stay silent to avoid gust chatter.
        var rule = new AnchorDragAlarmRule();
        var nav = BuildNav(anchored: true, maxR: 30, curR: 31);
        await Assert.That(rule.Check(Ctx(nav))).IsNull();
    }

    [Test]
    public async Task Anchored_OverBandByMargin_Fires()
    {
        // 33 m > 30 + 2 = 32 m trip threshold.
        var rule = new AnchorDragAlarmRule();
        var nav = BuildNav(anchored: true, maxR: 30, curR: 33);
        var a = rule.Check(Ctx(nav));
        await Assert.That(a).IsNotNull();
        await Assert.That(a!.Title).IsEqualTo("ANCHOR DRAG");
        await Assert.That(a.Severity).IsEqualTo(AlarmSeverity.Danger);
        await Assert.That(a.Message).Contains("33");
        await Assert.That(a.Message).Contains("30");
    }

    [Test]
    public async Task Anchored_Alarmed_StaysAlarmed_UntilSafelyInside()
    {
        // Hysteresis clears only once current drops below max - band.
        var rule = new AnchorDragAlarmRule();
        // Trip
        var t1 = rule.Check(Ctx(BuildNav(true, 30, 35)));
        await Assert.That(t1).IsNotNull();
        // Gust-back inside the upper band but above (max - band). Should stay alarmed.
        var t2 = rule.Check(Ctx(BuildNav(true, 30, 29)));
        await Assert.That(t2).IsNotNull();
        // Below the clear band (28 < 30 - 2). Alarm clears.
        var t3 = rule.Check(Ctx(BuildNav(true, 30, 27)));
        await Assert.That(t3).IsNull();
    }

    [Test]
    public async Task MissingRadiusData_NoAlarm()
    {
        // Server-anchor active but radius fields haven't landed yet.
        var rule = new AnchorDragAlarmRule();
        var nav = BuildNav(anchored: true);  // no maxR / curR
        await Assert.That(rule.Check(Ctx(nav))).IsNull();
    }

    [Test]
    public async Task NaN_Inputs_NoAlarm()
    {
        var rule = new AnchorDragAlarmRule();
        var nav = BuildNav(anchored: true, maxR: double.NaN, curR: 50);
        await Assert.That(rule.Check(Ctx(nav))).IsNull();
    }

    [Test]
    public async Task AnchorCleared_ResetsAlarmedLatch()
    {
        // After a trip, if the user raises anchor (AnchorActive becomes
        // false) the internal latch should clear so the next anchoring
        // starts fresh.
        var rule = new AnchorDragAlarmRule();
        rule.Check(Ctx(BuildNav(true, 30, 40)));    // trip
        rule.Check(Ctx(BuildNav(false)));            // raise anchor
        // Re-anchor, current well inside radius -> no alarm.
        await Assert.That(rule.Check(Ctx(BuildNav(true, 30, 20)))).IsNull();
    }

    [Test]
    public async Task RuleMetadata()
    {
        var rule = new AnchorDragAlarmRule();
        await Assert.That(rule.Title).IsEqualTo("ANCHOR DRAG");
        await Assert.That(rule.Priority).IsEqualTo(140);
        await Assert.That(rule.AutoClear).IsTrue();
    }

    // --- staleness gate: dying anchor sensor must not fire false positives ---

    [Test]
    public async Task StaleRadiusData_DoesNotFire()
    {
        // When the anchor-alarm plugin stops publishing (server crash,
        // network blip, GPS dead), the last currentRadius sticks on the
        // model forever. Raising a drag alarm off that frozen value
        // would be a false positive -- the boat may not actually be
        // dragging. The rule's staleness gate (FreshnessOf == Dead at
        // ~30s) silences the alarm; the HUD's separate freshness pill
        // is what flags the dropout to the helm.
        var nowProvider = new MutableNow { Value = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc) };
        var nav = new NavigationData(() => nowProvider.Value);
        nav.ApplyAnchorPosition(47.4, 8.5);
        nav.Apply("navigation.anchor.maxRadius", 30.0);
        // Apply a "dragging" current radius now, then advance the clock
        // past the Dead threshold (>30s) without applying a fresh value.
        nav.Apply("navigation.anchor.currentRadius", 40.0);
        nowProvider.Value = nowProvider.Value.AddSeconds(45);

        var rule = new AnchorDragAlarmRule();
        var ctx = new AlarmEvaluationContext(nav, [], new FakeSettings(), nowProvider.Value, _ => false);
        await Assert.That(rule.Check(ctx)).IsNull();
    }

    [Test]
    public async Task StaleRadiusData_WhilePreviouslyAlarmed_PreservesLatch()
    {
        // The rule documents an explicit choice: when the data goes
        // stale mid-alarm, we don't clear the internal _alarmed latch.
        // That way, once a fresh delta lands again with cur > upper,
        // the existing alarm continues uninterrupted rather than
        // re-counting the hysteresis dead-band as if from scratch.
        // Pinning it so a refactor that "tidied up" the latch reset
        // doesn't silently change the post-recovery behaviour.
        var nowProvider = new MutableNow { Value = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc) };
        var nav = new NavigationData(() => nowProvider.Value);
        nav.ApplyAnchorPosition(47.4, 8.5);
        nav.Apply("navigation.anchor.maxRadius", 30.0);
        nav.Apply("navigation.anchor.currentRadius", 40.0);

        var rule = new AnchorDragAlarmRule();
        // First pass: fresh data, alarm trips.
        var first = rule.Check(new AlarmEvaluationContext(
            nav, [], new FakeSettings(), nowProvider.Value, _ => false));
        await Assert.That(first).IsNotNull();

        // Sensor goes stale.
        nowProvider.Value = nowProvider.Value.AddSeconds(45);
        var stale = rule.Check(new AlarmEvaluationContext(
            nav, [], new FakeSettings(), nowProvider.Value, _ => false));
        await Assert.That(stale).IsNull();

        // A fresh delta on the same dragging value: the alarm continues
        // (no need to punch through the upper band again).
        nav.Apply("navigation.anchor.currentRadius", 31.0);  // inside upper band
        var resumed = rule.Check(new AlarmEvaluationContext(
            nav, [], new FakeSettings(), nowProvider.Value, _ => false));
        await Assert.That(resumed).IsNotNull();
        await Assert.That(resumed!.Title).IsEqualTo("ANCHOR DRAG");
    }

    private sealed class MutableNow { public DateTime Value { get; set; } }
}
