using OnaPlotter.Models;

namespace OnaPlotter.Services.Pois;

/// <summary>
/// Provider-agnostic surface for the marine-POI overlay. Production
/// is <see cref="OverpassPoiService"/>; tests inject a fake.
///
/// <para>Failure semantics: implementations return an empty list on
/// any transient error (offline, server down, malformed response,
/// rate-limited). The caller renders nothing extra in that case;
/// previously-cached markers keep showing because the controller
/// merges fresh results into a long-lived cache rather than wiping
/// it on each call.</para>
/// </summary>
public interface IMarinePoiService
{
    /// <summary>
    /// Fetch POIs for a viewport bbox + enabled category set. Returns
    /// the freshly-fetched list (which the controller merges into the
    /// persistent cache); the displayable union of cache + fresh is
    /// the controller's responsibility, not the service's.
    /// </summary>
    /// <param name="south">Leaflet south bound.</param>
    /// <param name="west">Leaflet west bound.</param>
    /// <param name="north">Leaflet north bound.</param>
    /// <param name="east">Leaflet east bound.</param>
    /// <param name="categories">Enabled categories. Empty -> empty
    /// result (no Overpass round-trip).</param>
    /// <param name="ct">Cancels in-flight HTTP when the helm pans
    /// again or the page tears down.</param>
    Task<IReadOnlyList<MarinePoi>> FetchAsync(
        double south, double west, double north, double east,
        IReadOnlySet<MarinePoiCategory> categories,
        CancellationToken ct = default);
}
