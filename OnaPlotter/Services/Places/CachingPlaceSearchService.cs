namespace OnaPlotter.Services.Places;

/// <summary>
/// Decorator: consult <see cref="PlaceSearchCache"/> before calling
/// the inner <see cref="IPlaceSearchService"/>; populate the cache
/// on a fresh hit. Wired in <c>Program.cs</c> as the helm-facing
/// <see cref="IPlaceSearchService"/> registration; the inner is
/// the live <see cref="PhotonPlaceSearchService"/>.
///
/// <para>This is a separate type from <see cref="PlaceSearchCache"/>
/// so each layer has one job: the cache stores / loads, the
/// decorator orchestrates. A future Phase 4 fallback (Nominatim)
/// can swap in as the inner without touching the cache layer.</para>
/// </summary>
public sealed class CachingPlaceSearchService : IPlaceSearchService
{
    private readonly IPlaceSearchService _inner;
    private readonly PlaceSearchCache _cache;

    public CachingPlaceSearchService(IPlaceSearchService inner, PlaceSearchCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    public async Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        // Cache hit returns immediately; no debounce / network.
        var cached = await _cache.TryGetAsync(query, ct);
        if (cached is not null) return cached;

        // Miss: ask the live provider, populate, return.
        var fresh = await _inner.SearchAsync(query, ct);
        if (fresh.Count > 0)
        {
            // Persist with a NON-cancellable token: we already have
            // the fresh result in hand; if the helm cancels via a
            // fresh keystroke between SearchAsync returning and
            // PutAsync completing, dropping the persist isn't a
            // failure -- it just means the next call hits the
            // network again. Without this guard, IJSRuntime threw
            // OperationCanceledException out of the localStorage
            // write, which surfaced to the helm as "API request
            // fires but no suggestions appear" (the OnInput catch
            // bailed on the cancellation before _results = results).
            try { await _cache.PutAsync(query, fresh, CancellationToken.None); }
            catch (OperationCanceledException) { /* persist gave up; result still returned */ }
        }
        return fresh;
    }
}
