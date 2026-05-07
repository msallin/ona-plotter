using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// LocalStorage-backed snapshot of an in-progress route edit. Holds
/// at most one draft (single-edit-at-a-time UX); a future extension
/// could promote to keyed-by-routeId if the helm asks for parallel
/// edits.
///
/// <para>Lifecycle: route-edit start (or each tick while editing)
/// calls <see cref="SaveAsync"/>; save-success and explicit Cancel
/// call <see cref="ClearAsync"/>; app-startup checks
/// <see cref="LoadAsync"/> and prompts the helm to restore if a
/// draft survives across reload (network drop, accidental tab
/// close, "logged out" save failure).</para>
/// </summary>
public interface IRouteDraftStore
{
    /// <summary>Read the persisted draft, or null when none exists
    /// or the stored JSON is malformed (schema-version mismatch,
    /// localStorage tampering). Caller treats null as "nothing to
    /// restore" - the prompt stays hidden.</summary>
    Task<RouteDraft?> LoadAsync(CancellationToken ct = default);

    /// <summary>Persist the current edit snapshot. Replaces any
    /// previous draft (single-slot store).</summary>
    Task SaveAsync(RouteDraft draft, CancellationToken ct = default);

    /// <summary>Drop the persisted draft. Called on save-success
    /// (no longer "unsaved") and on Cancel (helm explicitly
    /// discarded). Idempotent - a Clear with no draft present
    /// is a no-op.</summary>
    Task ClearAsync(CancellationToken ct = default);
}
