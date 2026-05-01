using System.Text.Json;
using OnaPlotter.Models;

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
            var draft = JsonSerializer.Deserialize<RouteDraft>(json);
            // Defensive: a draft with no coords is useless and
            // restoring it would land the helm in an empty edit
            // panel. Treat as "nothing to restore" rather than
            // surfacing a confusing prompt.
            if (draft is null || draft.Coords is null || draft.Coords.Length == 0)
                return null;
            return draft;
        }
        catch (JsonException)
        {
            // localStorage tampering, browser-extension corruption,
            // or a future schema change that bumps the model shape.
            // Treat as "no draft" -- the next save will overwrite
            // the malformed entry.
            return null;
        }
    }

    public Task SaveAsync(RouteDraft draft, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(draft);
        return _kv.SetAsync(StorageKey, json, ct);
    }

    public Task ClearAsync(CancellationToken ct = default) =>
        _kv.RemoveAsync(StorageKey, ct);
}
