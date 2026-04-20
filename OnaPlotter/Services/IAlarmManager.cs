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

    /// <summary>How many alarms are currently hidden by the cap on
    /// <see cref="ActiveAlarms"/>. UI uses this to show a "+N more"
    /// affordance so the user knows dismissing the top alarm will
    /// reveal another beneath. Zero when the stack is under cap.</summary>
    int HiddenAlarmsCount { get; }

    /// <summary>Targets the user has silenced, newest first. Each entry
    /// includes a label and expiry so the UI can render a countdown chip.</summary>
    IReadOnlyList<SnoozedTarget> SnoozedTargets { get; }

    /// <summary>Rules currently in a post-dismiss rearm window. UI
    /// renders one chip per entry next to the snooze chips so the
    /// helm can see why a just-dismissed alarm is silent. Evaluated
    /// per call so the remaining-seconds field is fresh on each
    /// render.</summary>
    IReadOnlyList<AlarmRearmInfo> RearmStatuses(DateTime now);

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

    /// <summary>Loads persisted snooze state from
    /// <see cref="IKeyValueStore"/>. Called once at app startup; safe
    /// to call again (no-op after the first). Failures (no storage,
    /// malformed JSON) degrade silently so a bad cache never blocks
    /// the rest of the app's boot.</summary>
    Task InitializeAsync();
}
