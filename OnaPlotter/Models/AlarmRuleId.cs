namespace OnaPlotter.Models;

/// <summary>
/// Stable identity of an alarm source, independent of the banner text.
/// Used for persistence (<c>DisabledAlarmRules</c>), the per-rule
/// Settings toggles, the manager's dismiss / cooldown keys and any UI
/// branching on "which kind of alarm is this". Display text lives on
/// <see cref="AlarmInfo.Title"/> and may change freely; this enum may not
/// be renamed without migrating the persisted keys.
/// <para>
/// When adding a new value:
/// 1. Add it here.
/// 2. Map it in <see cref="AlarmRuleIds.PersistedKey"/> (the compiler
///    flags the missing switch arm).
/// 3. Return it from the new rule's <c>IAlarmRule.Id</c> and stamp it on
///    every <see cref="AlarmInfo"/> the rule emits.
/// 4. Add an <c>AlarmRuleEnableToggle</c> on the Settings page if the
///    helm should be able to switch it off.
/// </para>
/// </summary>
public enum AlarmRuleId
{
    Shallow,
    AnchorDrag,
    AnchorTide,
    Deadman,
    Cpa,
    Hazard,
    WindShift,
    Approach,
    Sart,
    /// <summary>Server-sourced notification with no local rule
    /// counterpart (MOB, fire, remote collision, custom plugin alerts).
    /// Not switchable from Settings: the server owns these.</summary>
    ServerNotification,
}

/// <summary>Persistence mapping for <see cref="AlarmRuleId"/>.</summary>
public static class AlarmRuleIds
{
    /// <summary>
    /// Key stored in <c>disabledAlarmRules.v1</c>. The strings equal the
    /// historical rule titles so settings persisted before the id existed
    /// keep switching off the same rules. Null for ids that cannot be
    /// disabled by the helm.
    /// </summary>
    public static string? PersistedKey(this AlarmRuleId id) => id switch
    {
        AlarmRuleId.Shallow => "SHALLOW",
        AlarmRuleId.AnchorDrag => "ANCHOR DRAG",
        AlarmRuleId.AnchorTide => "ANCHOR TIDE",
        AlarmRuleId.Deadman => "DEADMAN",
        AlarmRuleId.Cpa => "CPA",
        AlarmRuleId.Hazard => "HAZARD",
        AlarmRuleId.WindShift => "WIND SHIFT",
        AlarmRuleId.Approach => "APPROACH",
        AlarmRuleId.Sart => "SART",
        AlarmRuleId.ServerNotification => null,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unmapped AlarmRuleId"),
    };

    /// <summary>True when the helm has switched this alarm source off.
    /// Ids without a persisted key are never disabled.</summary>
    public static bool IsDisabledIn(this AlarmRuleId id, IReadOnlySet<string> disabledKeys) =>
        id.PersistedKey() is { } key && disabledKeys.Contains(key);
}
