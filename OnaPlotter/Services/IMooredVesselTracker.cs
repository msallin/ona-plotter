using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// Decision interface for "is this AIS vessel moored / parked?". One
/// implementation today (heuristic + nav-state); registered as a singleton
/// so the CPA alarm pipeline and the Harbor-mode filter share a single
/// source of truth (and don't tick a parallel state ring per consumer).
/// </summary>
public interface IMooredVesselTracker
{
    /// <summary>True when the vessel should be treated as moored. Honours
    /// SK <c>navigation.state</c> when present, otherwise falls back to
    /// the SOG-dwell heuristic.</summary>
    bool IsMoored(AisVessel v, DateTime now);

    /// <summary>Drops dwell-state for vessels no longer in the active
    /// context set so a long session doesn't leak memory on every AIS
    /// target that ever appeared.</summary>
    void Cleanup(IReadOnlyCollection<string> activeContexts);
}
