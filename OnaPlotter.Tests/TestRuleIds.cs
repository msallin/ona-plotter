using OnaPlotter.Models;

namespace OnaPlotter.Tests;

/// <summary>
/// Maps the display titles used by stub alarm rules in tests to a stable
/// <see cref="AlarmRuleId"/>. Real rule titles map to their production
/// id; the placeholder titles ("A", "B", "NEAR", ...) map to distinct ids
/// so two stub rules in the same test never share an alarm identity.
/// </summary>
internal static class TestRuleIds
{
    public static AlarmRuleId For(string title) => title switch
    {
        "SHALLOW" => AlarmRuleId.Shallow,
        "CPA" => AlarmRuleId.Cpa,
        "WIND SHIFT" => AlarmRuleId.WindShift,
        "APPROACH" => AlarmRuleId.Approach,
        "DEADMAN" => AlarmRuleId.Deadman,
        "SART" => AlarmRuleId.Sart,
        "HAZARD" => AlarmRuleId.Hazard,
        "ANCHOR DRAG" => AlarmRuleId.AnchorDrag,
        "ANCHOR TIDE" => AlarmRuleId.AnchorTide,
        "A" or "FAR" => AlarmRuleId.AnchorDrag,
        "B" or "NEAR" => AlarmRuleId.AnchorTide,
        "C" or "LATCH" => AlarmRuleId.Deadman,
        "D" or "TOP" => AlarmRuleId.Hazard,
        "E" or "OTHER" => AlarmRuleId.Sart,
        _ => AlarmRuleId.ServerNotification,
    };
}
