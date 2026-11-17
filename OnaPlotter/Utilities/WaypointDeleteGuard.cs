using OnaPlotter.Models;

namespace OnaPlotter.Utilities;

/// <summary>Why a waypoint delete is refused, if it is.</summary>
public enum WaypointDeleteVerdict
{
    Allowed,
    /// <summary>MOB waypoints are persistent history.</summary>
    IsMob,
    /// <summary>Neither the page nor the store knows the id, so there is
    /// nothing to tell whether it is a MOB.</summary>
    Unknown,
}

/// <summary>
/// Trust-boundary check for deleting a waypoint. The page-local copy (a
/// popup's id, a rendered Layers row) can be stale: a waypoint that
/// becomes a MOB through a SignalK delta stays non-MOB locally until the
/// store event drains. Either copy saying MOB refuses the delete, and an
/// id neither copy knows is refused rather than guessed at.
/// </summary>
public static class WaypointDeleteGuard
{
    public static WaypointDeleteVerdict Check(SignalkWaypoint? local, SignalkWaypoint? stored)
    {
        if (local?.IsMob == true || stored?.IsMob == true) return WaypointDeleteVerdict.IsMob;
        if (local is null && stored is null) return WaypointDeleteVerdict.Unknown;
        return WaypointDeleteVerdict.Allowed;
    }
}
