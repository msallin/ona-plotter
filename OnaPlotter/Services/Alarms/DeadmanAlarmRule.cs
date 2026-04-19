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

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        double minutes = ctx.Settings.DeadmanTimeoutMinutes;
        if (minutes <= 0) return null;   // feature disabled

        var elapsed = ctx.Now - _tracker.LastInteractionUtc;
        if (elapsed.TotalMinutes < minutes) return null;

        return new AlarmInfo(
            Title: Title,
            Message: $"No interaction for {(int)elapsed.TotalMinutes} min -- still there?",
            Severity: AlarmSeverity.Warn,
            // TTI=0: "happening now", pins it above a pending CPA at TTI=5min.
            TimeToEventMinutes: 0);
    }
}
