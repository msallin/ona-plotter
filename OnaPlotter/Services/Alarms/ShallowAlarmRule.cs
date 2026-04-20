using OnaPlotter.Models;

namespace OnaPlotter.Services.Alarms;

/// <summary>SHALLOW: depth below the user's configured alarm threshold.
///
/// Auto-clears when depth rises back above threshold for a brief moment,
/// BUT once the user has dismissed the alarm we require a sustained
/// <see cref="RearmClearDuration"/> of non-shallow depth before the alarm
/// can re-fire. Without that, a boat sitting on a shoal with swell
/// bobbing it above and below threshold would retrigger the alarm every
/// few seconds even after the user acked it. 5 min is long enough to
/// mean "we've genuinely moved" but short enough to still catch the
/// next real grounding risk.
/// </summary>
public sealed class ShallowAlarmRule : IAlarmRule
{
    /// <summary>Duration the depth must remain above threshold after a
    /// user dismissal before the rule is allowed to re-fire. See class
    /// remarks for why.</summary>
    public static readonly TimeSpan RearmClearDuration = TimeSpan.FromMinutes(5);

    public string Title => "SHALLOW";
    public int Priority => 100;    // highest priority - grounding risk
    public bool AutoClear => true;

    // Dismissal state. Null = not in a post-dismiss cooldown; the rule
    // behaves as before and fires whenever depth < threshold.
    private DateTime? _dismissedAt;
    // First tick AFTER _dismissedAt that saw depth >= threshold. Rearm
    // timer is measured from here. Reset to null on any tick that goes
    // shallow again, so the 5 min must be CONTINUOUS.
    private DateTime? _sustainedClearFrom;

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        var d = ctx.Data.Depth;
        var threshold = ctx.Settings.DepthAlarmThreshold;

        // Walk the post-dismiss state machine FIRST. The idea: after the
        // user acks, don't re-arm the audio until we've been safely out
        // of the shallow band for a sustained window, even if depth
        // dips briefly during that window.
        if (_dismissedAt is not null)
        {
            if (d is null || d < threshold)
            {
                // Still / again shallow. Reset the sustained-clear
                // timer; stay quiet until we're clear long enough.
                _sustainedClearFrom = null;
                return null;
            }

            // Depth is above threshold this tick. Start (or continue)
            // measuring the sustained-clear window.
            _sustainedClearFrom ??= ctx.Now;
            if (ctx.Now - _sustainedClearFrom.Value < RearmClearDuration)
            {
                // Clear, but not for long enough yet. Stay quiet.
                return null;
            }

            // Sustained-clear duration met. Re-arm the rule by wiping
            // the dismissal state, then fall through to the usual
            // "is it shallow right now?" check (which it isn't, since
            // d >= threshold, but leave that to the one code path).
            _dismissedAt = null;
            _sustainedClearFrom = null;
        }

        if (d is null || d >= threshold) return null;
        // SHALLOW is happening now -- TTI=0 pins it above any pending
        // danger alarm (a CPA still 5min out).
        return new AlarmInfo(
            Title: Title,
            Message: $"Depth {d:F1}m < {threshold:F1}m",
            Severity: AlarmSeverity.Danger,
            TimeToEventMinutes: 0);
    }

    public void OnDismissed(AlarmInfo dismissed, DateTime at)
    {
        _dismissedAt = at;
        _sustainedClearFrom = null;
    }
}
