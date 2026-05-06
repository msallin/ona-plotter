namespace OnaPlotter.Services.Places;

/// <summary>
/// Decorator: try a primary <see cref="IPlaceSearchService"/>; if it
/// returns no results, ask a secondary as a fallback. Wired in
/// <c>Program.cs</c> as
/// <c>FallbackPlaceSearchService(Photon, Nominatim)</c> so that a
/// Photon outage / quota-throttle / regional gap still gets the helm
/// a hit from OSM Nominatim.
///
/// <para>Sequential by design (not parallel): under normal conditions
/// Photon answers immediately and Nominatim is never called, which
/// matters because Nominatim's 1-rps policy means parallel fan-out
/// would burn rate-limit budget on every keystroke. The worst-case
/// latency is bounded at primary timeout + secondary timeout (~8 s
/// with the current per-call caps), which is acceptable for an
/// uncommon offline / partial-outage path; the SearchBox shows a
/// spinner the whole time.</para>
///
/// <para>Cancellation propagates through both calls (helm typed
/// another character mid-fallback) so a slow secondary never clobbers
/// the dropdown for a now-stale query.</para>
/// </summary>
public sealed class FallbackPlaceSearchService : IPlaceSearchService
{
    private readonly IPlaceSearchService _primary;
    private readonly IPlaceSearchService _secondary;

    public FallbackPlaceSearchService(IPlaceSearchService primary, IPlaceSearchService secondary)
    {
        _primary = primary;
        _secondary = secondary;
    }

    public async Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var primaryResults = await _primary.SearchAsync(query, ct);
        if (primaryResults.Count > 0) return primaryResults;

        // Cancellation check: if the helm cancelled while we waited
        // on the primary, don't burn a Nominatim request slot for a
        // dropdown the user has already moved past.
        if (ct.IsCancellationRequested) return primaryResults;

        return await _secondary.SearchAsync(query, ct);
    }
}
