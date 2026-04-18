using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// Owns the active alarm stack shown in the top banner plus the state
/// needed to evaluate alarms: per-target snooze, moored-vessel auto-mute,
/// the debounce clock, and a short ring-buffer history of dismissed /
/// auto-cleared alarms. The component (MainLayout) is responsible for
/// rendering the banner and driving the audio side-effect via
/// <see cref="OnAlarmChanged"/>.
/// </summary>
public interface IAlarmManager
{
    /// <summary>The top-severity alarm in <see cref="ActiveAlarms"/>, or
    /// null if the stack is empty. Kept as a single-alarm convenience for
    /// audio-driver code and callers that only need the most-urgent entry.</summary>
    AlarmInfo? ActiveAlarm { get; }

    /// <summary>All currently-active alarms, ordered by severity descending
    /// then by rule priority ascending. Capped so a stuck condition cannot
    /// flood the banner; lower-priority alarms fall off when the cap is
    /// exceeded.</summary>
    IReadOnlyList<AlarmInfo> ActiveAlarms { get; }

    /// <summary>Targets the user has silenced, newest first. Each entry
    /// includes a label and expiry so the UI can render a countdown chip.</summary>
    IReadOnlyList<SnoozedTarget> SnoozedTargets { get; }

    /// <summary>Ring buffer of the last N alarms that left the active
    /// stack (user dismiss / user snooze / auto-clear), newest first.
    /// For the "why did it beep?" post-mortem drawer.</summary>
    IReadOnlyList<DismissedAlarm> DismissedHistory { get; }

    /// <summary>
    /// Fires when the top-severity alarm transitions (set / cleared /
    /// replaced / severity change) AND when the top alarm's message
    /// changes. The payload is the new top alarm captured at event-raise
    /// time so handlers don't race with a later Evaluate/Dismiss
    /// mutating state during their await. Use this for audio-cadence
    /// decisions.
    /// </summary>
    event Action<AlarmInfo?>? OnAlarmChanged;

    /// <summary>Fires when any part of the observable state changes:
    /// active stack, snoozed list, or dismissed history. UI-only; audio
    /// drivers should subscribe to <see cref="OnAlarmChanged"/> instead
    /// so they don't re-arm on purely cosmetic updates.</summary>
    event Action? OnAlarmsChanged;

    /// <summary>
    /// Fires alarm evaluation across all configured sources (depth, CPA,
    /// wind shift). Internally debounced so calling at 3 Hz is free. Pass
    /// the current navigation snapshot, AIS snapshot, and settings.
    /// </summary>
    void Evaluate(NavigationData data, IReadOnlyCollection<AisVessel> vessels, IAppSettings settings);

    /// <summary>Clears all active alarms and logs each as
    /// <see cref="DismissReason.UserDismissed"/>. No effect on an empty stack.</summary>
    Task DismissAsync();

    /// <summary>Clears one specific alarm from the active stack. Matches
    /// by (Title, TargetKey). Logs as <see cref="DismissReason.UserDismissed"/>.</summary>
    Task DismissAsync(AlarmInfo alarm);

    /// <summary>Snoozes the top-severity alarm's target for
    /// <see cref="SnoozeDurationMinutes"/> minutes and clears it from
    /// the banner. No-op when the top alarm has no target key.</summary>
    Task SnoozeActiveAsync();

    /// <summary>Snoozes one specific alarm's target. No-op when the alarm
    /// has no target key.</summary>
    Task SnoozeAsync(AlarmInfo alarm);

    /// <summary>Removes a target from the snooze list. The next Evaluate
    /// tick can re-raise the alarm.</summary>
    Task UnsnoozeAsync(string targetKey);

    /// <summary>Default snooze duration. Exposed so the UI can show "Snooze 10 m".</summary>
    int SnoozeDurationMinutes { get; }
}
