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
    /// because it runs on every Evaluate tick.
    /// </summary>
    AlarmInfo? Check(AlarmEvaluationContext ctx);
}

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
