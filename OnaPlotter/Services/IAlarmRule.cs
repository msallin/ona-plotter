using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// A single alarm-detection rule. The <see cref="AlarmManager"/> owns a
/// list of these and evaluates them in priority order on every Evaluate
/// tick. Adding a new alarm type is a matter of implementing this
/// interface and registering it in the DI container.
/// </summary>
public interface IAlarmRule
{
    /// <summary>Short uppercase title, also the alarm banner's <c>Title</c>
    /// field. Must be unique per rule.</summary>
    string Title { get; }

    /// <summary>
    /// Relative priority; rules with lower numbers fire first and short-
    /// circuit the remaining rules when they return an alarm. Conventions:
    /// 100 = shallow / grounding, 200 = collision, 300 = wind shift.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// When true, the banner auto-clears as soon as <see cref="Check"/>
    /// stops returning an alarm (SHALLOW, CPA). When false, the banner
    /// latches until the user dismisses it (WIND SHIFT - a transient
    /// notification, not a continuously-true condition).
    /// </summary>
    bool AutoClear { get; }

    /// <summary>
    /// Evaluates the rule against the current snapshot. Return null to say
    /// "no alarm"; return an AlarmInfo to raise / update. Must be cheap
    /// because it runs on every Evaluate tick. Single-output rules
    /// implement this; the manager wraps the result through
    /// <see cref="CheckMany"/>.
    /// </summary>
    AlarmInfo? Check(AlarmEvaluationContext ctx);

    /// <summary>
    /// Multi-output variant for rules that surface several simultaneous
    /// alarms (e.g. a server-notification feed where depth + anchor +
    /// collision can all be active at once). Default wraps
    /// <see cref="Check"/> as a one-or-zero sequence so existing
    /// single-output rules don't have to change. Override when the rule
    /// genuinely emits more than one alarm per tick.
    /// </summary>
    IEnumerable<AlarmInfo> CheckMany(AlarmEvaluationContext ctx)
    {
        var single = Check(ctx);
        if (single is not null) yield return single;
    }

    /// <summary>
    /// Called by <see cref="AlarmManager"/> immediately after the user
    /// dismisses an alarm whose <c>Title</c> matches this rule's. Default
    /// is a no-op; override when the rule needs to remember the dismissal
    /// to implement a per-rule rearm policy (e.g. SHALLOW requires the
    /// depth to be above threshold for 5 minutes before it re-fires).
    /// </summary>
    void OnDismissed(AlarmInfo dismissed, DateTime at) { }

    /// <summary>
    /// Exposes the rule's post-dismiss rearm state to the UI so a chip
    /// can tell the helm "SHALLOW armed again in 3m, waiting for clear
    /// depth". Returns null when the rule is either fully armed or has
    /// never been dismissed. Default implementation returns null; rules
    /// with a meaningful rearm window (currently only SHALLOW) override.
    /// <para>
    /// Note: manager-level <see cref="AlarmManager.DismissCooldownSeconds"/>
    /// is a baseline 30 s per-key cooldown that runs IN ADDITION to
    /// whatever a rule reports here. The manager-level cooldown is a
    /// lower bound -- the rule's rearm policy can extend it further
    /// (e.g. SHALLOW's 5 min of sustained-clear depth) but can't shorten it.
    /// </para>
    /// </summary>
    AlarmRearmInfo? GetRearmStatus(DateTime now) => null;
}

/// <summary>
/// Rule-level "alarm is temporarily suppressed, here's why" info,
/// surfaced next to the snooze-chips so the helm can see that an
/// alarm is intentionally silent rather than assuming it's broken.
/// </summary>
/// <param name="Title">Rule title, e.g. "SHALLOW".</param>
/// <param name="SecondsRemaining">Approximate seconds until the rule
/// can re-arm. May be 0 or negative if the rule is waiting on an
/// external condition (e.g. depth is still below threshold); UI should
/// treat non-positive values as "indefinite".</param>
/// <param name="Hint">Short human-readable reason, e.g.
/// "waiting 5 min of clear depth".</param>
public readonly record struct AlarmRearmInfo(
    string Title,
    double SecondsRemaining,
    string Hint);

/// <summary>
/// Read-only bag of everything a rule might need. Passed by value to keep
/// the rule implementations pure - no captured state, trivially testable.
/// </summary>
/// <param name="Data">Own-vessel navigation snapshot.</param>
/// <param name="Vessels">AIS snapshot at evaluation time.</param>
/// <param name="Settings">User-configurable thresholds.</param>
/// <param name="Now">UTC clock used for time-based logic (snooze, lookback).</param>
/// <param name="IsSnoozed">Predicate: given a target key (AIS context),
/// has the user snoozed alarms on it? Managed by AlarmManager.</param>
public readonly record struct AlarmEvaluationContext(
    NavigationData Data,
    IReadOnlyCollection<AisVessel> Vessels,
    IAppSettings Settings,
    DateTime Now,
    Func<string, bool> IsSnoozed);
