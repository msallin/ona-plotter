using OnaPlotter.Services.Settings;

namespace OnaPlotter.Services;

/// <summary>
/// Application-wide user preferences. Values are kept in memory and
/// persisted to <see cref="IKeyValueStore"/> on change.
///
/// <para>Composed from the narrow settings interfaces in
/// <c>Services/Settings/</c> (ARCH-003: ISP carve-out from the 43-
/// property God Interface). Existing consumers can still inject
/// <c>IAppSettings</c> for the full surface; new consumers should
/// inject only the narrow interface they actually depend on so test
/// doubles stay small and the change-amplification radius stays
/// scoped.</para>
///
/// <para>Cross-cutting members that don't belong to any one
/// concern stay here: the <see cref="OnSettingsChanged"/> event
/// (every consumer that wants any change notification observes
/// this single fan-out) and <see cref="InitializeAsync"/> (the
/// one-shot bootstrap that loads every persisted value).</para>
/// </summary>
public interface IAppSettings :
    IAlarmThresholds,
    IThemeSettings,
    IMapDisplaySettings,
    INavPreferences,
    IChartSettings,
    IPersistedView,
    IWindPageSettings
{
    /// <summary>Fires after any setter persists. Existing
    /// consumers do their own per-property diffing in handlers;
    /// future per-concern events can land on the narrow interfaces
    /// without breaking this fan-out.</summary>
    event Action? OnSettingsChanged;

    /// <summary>One-shot bootstrap. Loads every persisted value
    /// from <see cref="IKeyValueStore"/>. Safe to call multiple
    /// times -- subsequent calls re-read storage.</summary>
    Task InitializeAsync();
}
