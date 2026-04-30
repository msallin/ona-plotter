using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// Read-only snapshot of the regions currently known to the client.
/// Populated by Map.razor on every region-resource refresh; consumed
/// by alarm rules that need to query regions per evaluation tick
/// (notably <c>HazardousRegionAlarmRule</c>).
///
/// <para>Mirrors the shape of <see cref="AisStore"/>: a singleton
/// the page mutates when fresh data arrives, and rules read from at
/// alarm-tick cadence. Rules don't get regions threaded through
/// <see cref="AlarmEvaluationContext"/> because regions only matter
/// to one rule today and the rule injecting its own dependency keeps
/// the context shape minimal for every other rule.</para>
/// </summary>
public interface IRegionStore
{
    /// <summary>Latest snapshot of the loaded regions. Replace-on-write
    /// (the implementation hands back the same reference between
    /// updates), so callers can store the reference for cheap
    /// comparison if they want; the list itself is read-only.</summary>
    IReadOnlyList<SignalkRegion> Regions { get; }

    /// <summary>Replace the snapshot with a fresh list. Called from
    /// Map.razor's region-resource refresh path. Null is treated as
    /// empty so callers don't have to null-check before pushing the
    /// result of a failed REST round-trip.</summary>
    void SetRegions(IReadOnlyList<SignalkRegion>? regions);
}
