using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the "fires exactly when the inactivity window is exceeded" contract
/// of the DEADMAN alarm rule. Uses <see cref="DeadmanTracker.ForceLastInteraction"/>
/// to inject fake last-interaction times so the test doesn't have to wait
/// in real wall-clock seconds.
/// </summary>
public class DeadmanAlarmRuleTests
{
    private static AlarmEvaluationContext Ctx(DateTime now, double timeoutMinutes) =>
        new(new NavigationData(), [],
            new FakeSettings { DeadmanTimeoutMinutes = timeoutMinutes },
            now,
            _ => false);

    [Test]
    public async Task Disabled_When_Timeout_Is_Zero()
    {
        // Default is 0 = off. Long-dormant tracker must not fire.
        var tracker = new DeadmanTracker();
        tracker.ForceLastInteraction(DateTime.UtcNow.AddHours(-2));
        var rule = new DeadmanAlarmRule(tracker);

        await Assert.That(rule.Check(Ctx(DateTime.UtcNow, 0))).IsNull();
    }

    [Test]
    public async Task Silent_While_Inside_Window()
    {
        var now = DateTime.UtcNow;
        var tracker = new DeadmanTracker();
        tracker.ForceLastInteraction(now.AddMinutes(-4));
        var rule = new DeadmanAlarmRule(tracker);

        // 4 min elapsed, 5 min window: quiet.
        await Assert.That(rule.Check(Ctx(now, 5))).IsNull();
    }

    [Test]
    public async Task Fires_When_Window_Exceeded()
    {
        var now = DateTime.UtcNow;
        var tracker = new DeadmanTracker();
        tracker.ForceLastInteraction(now.AddMinutes(-6));
        var rule = new DeadmanAlarmRule(tracker);

        var alarm = rule.Check(Ctx(now, 5));
        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Title).IsEqualTo("DEADMAN");
        await Assert.That(alarm.Severity).IsEqualTo(AlarmSeverity.Warn);
        // TTI=0 means "happening now"; this pins it above a pending
        // CPA with TTI=5 min when the manager sorts the stack.
        await Assert.That(alarm.TimeToEventMinutes).IsEqualTo(0);
    }

    [Test]
    public async Task Touch_Clears_On_Next_Evaluate()
    {
        // Simulates the user tapping the map: tracker.Touch() resets
        // LastInteractionUtc, rule goes silent again.
        var tracker = new DeadmanTracker();
        tracker.ForceLastInteraction(DateTime.UtcNow.AddMinutes(-10));
        var rule = new DeadmanAlarmRule(tracker);

        await Assert.That(rule.Check(Ctx(DateTime.UtcNow, 5))).IsNotNull();

        tracker.Touch();  // user just tapped
        await Assert.That(rule.Check(Ctx(DateTime.UtcNow, 5))).IsNull();
    }

    [Test]
    public async Task AutoClear_Is_True_So_Tap_Clears_Banner()
    {
        await Assert.That(new DeadmanAlarmRule(new DeadmanTracker()).AutoClear).IsTrue();
    }

    [Test]
    public async Task Escalates_To_Danger_Past_Double_The_Window()
    {
        // After 2x the timeout without interaction, the warn-level
        // "still there?" becomes a danger-level "WAKE UP". Sleeping
        // watchkeeper may have already slept through the first Warn.
        var now = DateTime.UtcNow;
        var tracker = new DeadmanTracker();
        tracker.ForceLastInteraction(now.AddMinutes(-11));
        var rule = new DeadmanAlarmRule(tracker);

        // Timeout 5 min, elapsed 11 min > 2x5 = 10 min -> Danger.
        var alarm = rule.Check(Ctx(now, 5));
        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Severity).IsEqualTo(AlarmSeverity.Danger);

        // At the boundary (exactly 2x), Danger kicks in. This is the
        // branch that would off-by-one if someone wrote `>` instead of `>=`.
        tracker.ForceLastInteraction(now.AddMinutes(-10));
        alarm = rule.Check(Ctx(now, 5));
        await Assert.That(alarm!.Severity).IsEqualTo(AlarmSeverity.Danger);

        // Just under 2x -> still Warn.
        tracker.ForceLastInteraction(now.AddMinutes(-9));
        alarm = rule.Check(Ctx(now, 5));
        await Assert.That(alarm!.Severity).IsEqualTo(AlarmSeverity.Warn);
    }

    [Test]
    public async Task Night_Mode_Uses_Shorter_Window_When_Configured()
    {
        // Night-mode-on with a 15 min override shortens the watch
        // window from the day value (say, 0 / off). A 16-min idle
        // triggers at night but wouldn't have during the day.
        var now = DateTime.UtcNow;
        var tracker = new DeadmanTracker();
        tracker.ForceLastInteraction(now.AddMinutes(-16));
        var rule = new DeadmanAlarmRule(tracker);

        var dayCtx = new AlarmEvaluationContext(new NavigationData(), [],
            new FakeSettings { DeadmanTimeoutMinutes = 0, DeadmanNightMinutes = 15, NightMode = false },
            now, _ => false);
        await Assert.That(rule.Check(dayCtx)).IsNull();

        var nightCtx = new AlarmEvaluationContext(new NavigationData(), [],
            new FakeSettings { DeadmanTimeoutMinutes = 0, DeadmanNightMinutes = 15, NightMode = true },
            now, _ => false);
        var alarm = rule.Check(nightCtx);
        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Severity).IsEqualTo(AlarmSeverity.Warn);
    }

    [Test]
    public async Task Night_Override_Zero_Falls_Back_To_Day_Value()
    {
        // Users who don't want a night-specific override set DeadmanNightMinutes
        // to 0; the day value applies at night too (legacy behaviour).
        var now = DateTime.UtcNow;
        var tracker = new DeadmanTracker();
        tracker.ForceLastInteraction(now.AddMinutes(-6));
        var rule = new DeadmanAlarmRule(tracker);

        var ctx = new AlarmEvaluationContext(new NavigationData(), [],
            new FakeSettings { DeadmanTimeoutMinutes = 5, DeadmanNightMinutes = 0, NightMode = true },
            now, _ => false);

        // Day value (5 min) applied because night override is 0. 6 > 5 -> fire.
        await Assert.That(rule.Check(ctx)).IsNotNull();
    }
}
