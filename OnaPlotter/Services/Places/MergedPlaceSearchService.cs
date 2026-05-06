namespace OnaPlotter.Services.Places;

/// <summary>
/// Decorator that prepends own-data hits ahead of online geocoder
/// results in the search dropdown. Composition is the helm-facing
/// IPlaceSearchService:
///
///   MergedPlaceSearchService(OwnPlacesIndex, CachingPlaceSearchService(Photon))
///
/// Own-data first because the helm searching "anchorage cay" is
/// almost always looking for THEIR saved anchorage, not the OSM
/// entry of the same name on the other side of the ocean.
///
/// <para>Both lookups run in parallel: even if Photon is slow, the
/// own-data hits land in the dropdown without waiting on the
/// network. The SearchBox renders a separator between source-tiers
/// (own-data Source values: "waypoint" / "note" / "region";
/// online: "photon" / "nominatim") so the helm reads the two
/// blocks distinctly.</para>
/// </summary>
public sealed class MergedPlaceSearchService : IPlaceSearchService
{
    private readonly OwnPlacesIndex _own;
    private readonly IPlaceSearchService _online;

    public MergedPlaceSearchService(OwnPlacesIndex own, IPlaceSearchService online)
    {
        _own = own;
        _online = online;
    }

    public async Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var ownTask = _own.SearchAsync(query, ct);
        var onlineTask = _online.SearchAsync(query, ct);
        await Task.WhenAll(ownTask, onlineTask);

        // Own-data first, online second. No de-duplication: a helm's
        // saved "Berlin" waypoint and the OSM "Berlin" hit are distinct
        // (different lat/lon, different intent). The Source badge in
        // the SearchBox UI tells them apart.
        var combined = new List<PlaceResult>(ownTask.Result.Count + onlineTask.Result.Count);
        combined.AddRange(ownTask.Result);
        combined.AddRange(onlineTask.Result);
        return combined;
    }
}
