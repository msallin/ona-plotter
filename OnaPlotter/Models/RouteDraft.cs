using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

/// <summary>
/// In-progress route edit serialised to localStorage. Captures
/// everything needed to resume the edit on a fresh page load:
/// the coords (lat/lon pairs, Leaflet order), the helm-typed name,
/// and the source route's id when editing an existing route in
/// place. The save instant is recorded so the restore prompt can
/// say "from 12 minutes ago" rather than just "your last session".
///
/// <para>Schema versioning: the storage key
/// (<c>route.draft.v1</c>) carries the version. A future model
/// change that's not backward-compatible bumps to v2, leaving v1
/// drafts orphaned in localStorage where the helm can drop them
/// via the browser's storage tools. We don't try to migrate -
/// the population of helms with mid-edit drafts at the moment of
/// a schema bump is small enough to live with the loss.</para>
/// </summary>
public sealed record RouteDraft(
    /// <summary>Source route id when the draft is editing an
    /// existing server-stored route. Null when the helm was
    /// building a fresh route. The restore flow reads this to
    /// decide whether to enter "edit existing" or "create new"
    /// mode.</summary>
    [property: JsonPropertyName("routeId")] string? RouteId,
    /// <summary>Helm-typed route name. May be empty (the save
    /// flow stamps a date-default if so). Round-tripped so
    /// re-restored drafts pick up where the helm left off in the
    /// name field too.</summary>
    [property: JsonPropertyName("name")] string? Name,
    /// <summary>Vertex list in Leaflet [lat, lon] order. Kept as
    /// a flat double[][] (not a typed wrapper) so it round-trips
    /// through System.Text.Json without a custom converter.</summary>
    [property: JsonPropertyName("coords")] double[][] Coords,
    /// <summary>UTC instant the draft was last persisted, ISO
    /// 8601. Used for the "from N minutes ago" hint on the
    /// restore prompt. String (not DateTime) because the JSON
    /// round-trip stays explicit - DateTime parsing modes have
    /// bitten this codebase before.</summary>
    [property: JsonPropertyName("savedAt")] string SavedAtIso);
