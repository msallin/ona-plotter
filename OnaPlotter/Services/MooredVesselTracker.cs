using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// Classifies an AIS vessel as "moored" when its SOG is under
/// <see cref="MooredSpeedThresholdMs"/> (~1 kn). Alarm rules use this
/// to skip parked / anchored / holding-station targets that would
/// otherwise flood the helmsman with false-positive CPAs in crowded
/// harbours.
/// <para>
/// Earlier revisions kept a per-vessel dwell timer (2 min of low speed
/// before a vessel was considered moored). That state was never
/// actually required for the safety trade: a vessel briefly dipping
/// below the threshold is skipped for one tick and re-enters the
/// projection on the next -- which costs at most one AIS sample of
/// delayed alarm and saves maintaining a dictionary. The class is
/// therefore stateless now; the name is kept for its call-site
/// semantics ("is this vessel to be treated as moored?").
/// </para>
/// </summary>
public sealed class MooredVesselTracker
{
    /// <summary>Below this SOG (m/s, ~1 kn) a vessel is treated as
    /// moored. The cutoff deliberately bands slow-drifting anchored
    /// boats, fishing vessels jogging on station, and ferries waiting
    /// for a berth -- the classic false-positive CPA sources.</summary>
    public const double MooredSpeedThresholdMs = 0.514;

    /// <summary>True when the vessel's current SOG is below
    /// <see cref="MooredSpeedThresholdMs"/>. Null SOG is not moored
    /// (no data, assume motion).</summary>
    public bool IsMoored(AisVessel v) =>
        v.SpeedOverGround is double sog && sog < MooredSpeedThresholdMs;
}
