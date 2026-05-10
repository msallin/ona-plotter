namespace OnaPlotter.Services.Resources;

using OnaPlotter.Models;

/// <summary>
/// Narrow read-only view of <see cref="ResourceStore"/>'s waypoint
/// cache. Extracted so consumers that only need to look up cached
/// waypoints (today: <c>MobService.FindMobWaypoint</c>) can be unit-
/// tested with a list-backed fake instead of standing up the full
/// resource subsystem (HTTP + WS + dedup + change events).
/// <para>The shape is deliberately tiny - if a future consumer needs
/// more than these two operations, prefer adding methods here over
/// reaching into <see cref="ResourceStore"/> directly. A growing
/// surface here is a signal that the consumer should subscribe to
/// the store's change events instead of polling.</para>
/// </summary>
public interface IWaypointReader
{
    /// <summary>Snapshot of all waypoints currently in the cache.
    /// Iteration order is dictionary-iteration order on the
    /// underlying cache (effectively insertion order on .NET; not
    /// guaranteed by spec but stable enough for the helm-facing
    /// scan in <c>MobService.FindMobWaypoint</c>).</summary>
    IReadOnlyList<SignalkWaypoint> Waypoints { get; }

    /// <summary>Look up a waypoint by id. Returns null when the
    /// waypoint isn't in the cache.</summary>
    SignalkWaypoint? GetWaypoint(string id);
}
