using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Alarms;

/// <summary>
/// SART / MOB / EPIRB: fires the moment an AIS target with a
/// distress-transmitter MMSI appears on the net. Doesn't care about
/// distance, heading, CPA, or buddy status -- the beacon's existence
/// IS the alarm. Cannot be snoozed (life-safety). Auto-clears when
/// the target drops off AIS (beacon switched off or out of range).
///
/// <para>MMSI ranges (ITU-R M.585-9):
/// 970xxxxxx = SART, 972xxxxxx = MOB, 974xxxxxx = EPIRB.</para>
///
/// <para>Priority 50 puts it above SHALLOW (100). A Man-Overboard
/// beacon in range is more urgent than almost any other condition
/// the boat can see, because someone's life depends on the response.
/// If the boat is also aground (SHALLOW), both alarms stack.</para>
/// </summary>
public sealed class AisSartAlarmRule : IAlarmRule
{
    public string Title => "SART";
    public int Priority => 50;
    public bool AutoClear => true;

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        // First pass: classify every vessel; emit alarm on the
        // closest distress beacon (there's usually one, but if two
        // SARTs are broadcasting we should show the nearer).
        AisVessel? nearest = null;
        string? nearestCategory = null;
        double nearestNm = double.MaxValue;

        foreach (var v in ctx.Vessels)
        {
            var category = AisSart.CategoryFromAny(v.Mmsi, v.Context);
            if (category is null) continue;

            double distNm = DistanceNm(ctx.Data, v);
            // `nearest is null` covers the case where we don't have
            // own-boat position yet: distNm is MaxValue for every
            // vessel, so the plain `<` check would never update the
            // pick. First beacon wins then; whichever shows next.
            if (nearest is null || distNm < nearestNm)
            {
                nearestNm = distNm;
                nearest = v;
                nearestCategory = category;
            }
        }

        if (nearest is null || nearestCategory is null) return null;

        string name = nearest.Name ?? nearest.Mmsi ?? "beacon";
        string distStr = double.IsFinite(nearestNm) && nearestNm < double.MaxValue
            ? $" {nearestNm:F2}nm"
            : "";

        return new AlarmInfo(
            Title: nearestCategory,
            Message: $"{name}{distStr}",
            Severity: AlarmSeverity.Danger,
            TargetKey: nearest.Context,
            TargetLabel: name,
            Snoozeable: false,                   // life-safety: no snooze
            TimeToEventMinutes: 0);              // a beacon is live, not pending
    }

    /// <summary>Equirectangular approximation, fine for at-a-glance
    /// distance display. Returns <c>double.MaxValue</c> when either
    /// side lacks a position so the caller can still pick a target.</summary>
    private static double DistanceNm(NavigationData own, AisVessel v)
    {
        if (own.Latitude is not double ownLat || own.Longitude is not double ownLon
            || v.Latitude is not double vLat || v.Longitude is not double vLon)
            return double.MaxValue;
        const double NmPerDegLat = 60.0;
        double dLat = (vLat - ownLat) * NmPerDegLat;
        double dLon = (vLon - ownLon) * NmPerDegLat * Math.Cos((ownLat + vLat) * 0.5 * Math.PI / 180);
        return Math.Sqrt(dLat * dLat + dLon * dLon);
    }
}
