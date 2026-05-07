using OnaPlotter.Models;

namespace OnaPlotter.Services.Alarms;

/// <summary>
/// DEADMAN: fires if the user hasn't interacted with the Map page for the
/// configured timeout (settings.DeadmanTimeoutMinutes). Safety net for
/// solo watchkeepers who might fall asleep or be incapacitated.
/// AutoClear=true so a tap that refreshes DeadmanTracker.LastInteractionUtc
/// clears the banner on the next Evaluate tick.
/// </summary>
/// <remarks>
/// The rule is dormant when the timeout is 0 (feature off) or the
/// deadline hasn't been reached. Priority sits between grounding (100)
/// and collision (200) so a deadman warning doesn't hide a CPA alarm
/// that's happening right now.
/// </remarks>
public sealed class DeadmanAlarmRule : IAlarmRule
{
    private readonly DeadmanTracker _tracker;

    public DeadmanAlarmRule(DeadmanTracker tracker)
    {
        _tracker = tracker;
    }

    public string Title => "DEADMAN";
    public int Priority => 150;
    public bool AutoClear => true;

    /// <summary>Cross-plotter publish path. The bridge rule on
    /// receivers falls through to the leaf-segment uppercase fallback
    /// for <c>helm.deadman</c>, surfacing Title="DEADMAN" on remote
    /// banners.</summary>
    public string? GetPublishPath(AlarmInfo alarm) =>
        "notifications.helm.deadman";

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        // Night mode runs a tighter watch window if the user has set one
        // (default 15 min). Sleeping watchkeepers in the cockpit want a
        // shorter leash than the daytime 0-off default.
        double minutes = ctx.Settings.NightMode && ctx.Settings.DeadmanNightMinutes > 0
            ? ctx.Settings.DeadmanNightMinutes
            : ctx.Settings.DeadmanTimeoutMinutes;
        if (minutes <= 0) return null;   // feature disabled

        var elapsed = ctx.Now - _tracker.LastInteractionUtc;
        if (elapsed.TotalMinutes < minutes) return null;

        // Escalate to Danger if the user has slept past 2x the configured
        // window. Warn severity is the 3-second cadence; Danger is 1-second,
        // louder, and requires hold-to-dismiss. This is the "still there?"
        // becoming "WAKE UP" transition - matches ship's-watch practice
        // where the deck speaker gets triggered after a missed first check.
        // Honoured by the dismiss-cooldown escalation-bypass so a warn
        // dismiss doesn't silence a subsequent danger.
        var severity = elapsed.TotalMinutes >= 2 * minutes
            ? AlarmSeverity.Danger
            : AlarmSeverity.Warn;

        return new AlarmInfo(
            Title: Title,
            Message: $"No interaction for {(int)elapsed.TotalMinutes} min - still there?",
            Severity: severity,
            // TTI=0: "happening now", pins it above a pending CPA at TTI=5min.
            TimeToEventMinutes: 0);
    }
}
