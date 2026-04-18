using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// Owns the single active alarm shown in the top banner plus the state
/// needed to evaluate alarms: per-target snooze, moored-vessel auto-mute,
/// and the debounce clock. The component (MainLayout) is responsible for
/// rendering the banner and driving the audio side-effect via
/// <see cref="OnAlarmChanged"/>.
/// </summary>
public interface IAlarmManager
{
    AlarmInfo? ActiveAlarm { get; }

    /// <summary>
    /// Fires when ActiveAlarm transitions (set / cleared / replaced) AND
    /// when an existing alarm's message changes. The payload is the new
    /// state, captured at event-raise time so handlers don't race with a
    /// later Evaluate/Dismiss mutating ActiveAlarm during their await.
    /// </summary>
    event Action<AlarmInfo?>? OnAlarmChanged;

    /// <summary>
    /// Fires alarm evaluation across all configured sources (depth, CPA,
    /// wind shift). Internally debounced so calling at 3 Hz is free. Pass
    /// the current navigation snapshot, AIS snapshot, and settings.
    /// </summary>
    void Evaluate(NavigationData data, IReadOnlyCollection<AisVessel> vessels, IAppSettings settings);

    /// <summary>Clears the current alarm. No effect if no alarm is active.</summary>
    Task DismissAsync();

    /// <summary>Snoozes the current alarm's target (if any) for
    /// <see cref="SnoozeDurationMinutes"/> minutes and clears the banner.
    /// No-op when the alarm doesn't carry a target key.</summary>
    Task SnoozeActiveAsync();

    /// <summary>Default snooze duration. Exposed so the UI can show "Snooze 10 m".</summary>
    int SnoozeDurationMinutes { get; }
}
