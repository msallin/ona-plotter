namespace OnaPlotter.Services.Resources;

using OnaPlotter.Models;

/// <summary>
/// Narrow consumer-side view of <see cref="ResourceStore"/>'s waypoint
/// cache. Extracted so consumers that only need to look up or
/// optimistically seed cached waypoints (today: <c>MobService</c>) can
/// be unit-tested with a list-backed fake instead of standing up the
/// full resource subsystem (HTTP + WS + dedup + change events).
/// <para>The shape is deliberately tiny - if a future consumer needs
/// more than these three operations, prefer adding methods here over
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

    /// <summary>Optimistically seed a client-built waypoint into the
    /// cache. Same code path as a WS-delta upsert - subscribers to
    /// <c>OnWaypointChanged</c> fire so the chart renders the marker
    /// immediately. Used by <c>MobService.RaiseAsync</c> to make the
    /// MOB chart pin visible even before (or when) the server-side
    /// PUT lands; when the eventual WS echo arrives it goes through
    /// the normal delta path, which content-equality dedups against
    /// the locally-applied entry.
    /// <para>Idempotent on <c>wp.Id</c>: a second call with the same
    /// id and identical content is a dedup no-op; a call with the
    /// same id but different content overwrites and fires Changed
    /// (matches the WS-delta contract).</para></summary>
    void ApplyLocal(SignalkWaypoint wp);
}
