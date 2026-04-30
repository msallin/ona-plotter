using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Alarms;

/// <summary>
/// HAZARD: fires while own-ship is inside any region whose
/// <see cref="SignalkRegion.IsHazard"/> flag is true. Turns regions
/// from decorative shapes into real safety geometry -- the helm draws
/// a polygon over a reef / no-go area / racing exclusion zone, ticks
/// "hazard", and a Danger banner appears whenever the boat enters it.
///
/// <para>Multi-region semantics: every hazardous region the boat sits
/// inside emits its own alarm via <see cref="IAlarmRule.CheckMany"/>.
/// Two overlapping hazards (e.g. "Shipping lane" + "No anchorage")
/// stack as two banners with distinct titles, so the helm can
/// dismiss them independently. The shared rule title "HAZARD" is
/// suffixed with the region's name; the AlarmManager keys on
/// (title, target) so per-region keys keep dismiss / cooldown state
/// separate.</para>
///
/// <para>Auto-clear: once the boat exits the region, the alarm
/// disappears on its own (no helm action required). That matches
/// every other geometry-driven rule (SHALLOW, CPA) and avoids
/// stranding a banner after the helm has already left the danger.</para>
///
/// <para>Priority 150 sits between SHALLOW (100; grounding now) and
/// CPA (200; collision soon). A hazard is a sustained-state warning
/// of "you should not be here", which fits between the two
/// time-flavoured tiers.</para>
/// </summary>
public sealed class HazardousRegionAlarmRule : IAlarmRule
{
    public string Title => "HAZARD";
    public int Priority => 150;
    public bool AutoClear => true;

    private readonly IRegionStore _regions;

    public HazardousRegionAlarmRule(IRegionStore regions) => _regions = regions;

    /// <summary>Single-output Check is unused in favour of the
    /// CheckMany overload below; AlarmManager calls CheckMany when
    /// it's overridden. Returning null here keeps the IAlarmRule
    /// contract honest for any caller that bypasses CheckMany.</summary>
    public AlarmInfo? Check(AlarmEvaluationContext ctx) => null;

    public IEnumerable<AlarmInfo> CheckMany(AlarmEvaluationContext ctx)
    {
        var data = ctx.Data;
        if (data.Latitude is not double lat || data.Longitude is not double lon)
            yield break;

        // Iterate the snapshot directly; the store hands back a
        // read-only reference that's safe to walk without copying.
        // No allocations on the hot path beyond the yielded
        // AlarmInfo records (one per active hazard the boat sits in).
        foreach (var region in _regions.Regions)
        {
            if (!region.IsHazard) continue;
            if (!IsInside(region, lat, lon)) continue;

            string label = !string.IsNullOrWhiteSpace(region.Name) ? region.Name!
                : !string.IsNullOrWhiteSpace(region.Id) ? region.Id
                : "(unnamed hazard)";
            // TargetKey = region id so AlarmManager's per-key
            // dismiss + cooldown state tracks each hazard
            // independently. A region with no Id (test-fixture
            // edge case) still produces a stable key from the
            // label.
            string targetKey = string.IsNullOrEmpty(region.Id) ? label : region.Id;

            yield return new AlarmInfo(
                Title: Title,
                Message: $"Inside {label}",
                Severity: AlarmSeverity.Danger,
                TargetKey: targetKey,
                TargetLabel: label,
                TimeToEventMinutes: null);
        }
    }

    /// <summary>True when the (lat, lon) sits inside any of the
    /// region's outer rings. Region rings come pre-extracted as
    /// Leaflet-order <c>[lat, lon]</c> arrays from
    /// <see cref="OnaPlotter.Services.Api.RegionApi.ExtractOuterRings"/>;
    /// a multi-polygon region returns true if any of its outer rings
    /// contains the point. Holes are not modelled (consistent with
    /// the rest of the region pipeline).</summary>
    private static bool IsInside(SignalkRegion region, double lat, double lon)
    {
        foreach (var ring in region.OuterRings)
        {
            if (PointInPolygon.Contains(ring, lat, lon)) return true;
        }
        return false;
    }

    /// <summary>Per-region notification path so cross-plotter
    /// publishes don't collapse all hazards to one channel. Same
    /// sanitisation pattern as CpaAlarmRule -- the region id is
    /// caller-controlled and could contain path separators that
    /// would extend the notifications hierarchy.</summary>
    public string? GetPublishPath(AlarmInfo alarm)
    {
        if (string.IsNullOrEmpty(alarm.TargetKey)) return null;
        return SanitisePerTargetPath(
            "notifications.security.hazard", alarm.TargetKey);
    }

    private static string SanitisePerTargetPath(string prefix, string targetKey)
    {
        var sb = new System.Text.StringBuilder(targetKey.Length);
        foreach (var c in targetKey)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return $"{prefix}.{sb.ToString()}";
    }
}
