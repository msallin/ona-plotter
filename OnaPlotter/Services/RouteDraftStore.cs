using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Json;

namespace OnaPlotter.Services;

/// <summary>localStorage-backed implementation. Storage key carries
/// a schema version (<c>route.draft.v1</c>); a future
/// non-backward-compatible model change bumps to v2 and leaves v1
/// drafts as orphans. Tests inject the in-memory key-value fake.
/// </summary>
public sealed class RouteDraftStore : IRouteDraftStore
{
    /// <summary>Storage key. Versioned so a model-shape change can
    /// land without resurrecting old drafts in a new schema.</summary>
    public const string StorageKey = "route.draft.v1";

    private readonly IKeyValueStore _kv;

    public RouteDraftStore(IKeyValueStore kv) => _kv = kv;

    public async Task<RouteDraft?> LoadAsync(CancellationToken ct = default)
    {
        var json = await _kv.GetAsync(StorageKey, ct);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var draft = JsonSerializer.Deserialize(json, OnaJsonContext.Default.RouteDraft);
            // Defensive: a draft with no coords is useless and
            // restoring it would land the helm in an empty edit
            // panel. Treat as "nothing to restore" rather than
            // surfacing a confusing prompt.
            if (draft is null || draft.Coords is null || draft.Coords.Length == 0)
                return null;
            // Per-coord shape + range validation. localStorage holds
            // user-controllable data: a browser extension, a typo in
            // a manual JSON edit, or a schema-version skew between
            // builds can land malformed entries here. Without this
            // check the JS renderer hits NaN-arithmetic on an
            // unbounded lat/lon and the route polyline silently
            // disappears, or the in-edit-panel waypoint list shows
            // "NaN, NaN" rows that look like a regression.
            //
            // Each coord is [lat, lon], lat in [-90, 90], lon in
            // [-180, 180], both finite. A single bad coord taints the
            // whole draft - partial restore is worse than "no draft"
            // because the helm gets a recovery prompt that loads
            // garbage.
            foreach (var c in draft.Coords)
            {
                if (c is null || c.Length != 2) return null;
                double lat = c[0], lon = c[1];
                if (!double.IsFinite(lat) || !double.IsFinite(lon)) return null;
                if (lat < -90.0 || lat > 90.0) return null;
                if (lon < -180.0 || lon > 180.0) return null;
            }
            return draft;
        }
        catch (JsonException)
        {
            // localStorage tampering, browser-extension corruption,
            // or a future schema change that bumps the model shape.
            // Treat as "no draft" - the next save will overwrite
            // the malformed entry.
            return null;
        }
    }

    public Task SaveAsync(RouteDraft draft, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(draft, OnaJsonContext.Default.RouteDraft);
        return _kv.SetAsync(StorageKey, json, ct);
    }

    public Task ClearAsync(CancellationToken ct = default) =>
        _kv.RemoveAsync(StorageKey, ct);
}
