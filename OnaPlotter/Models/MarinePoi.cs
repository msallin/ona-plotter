using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

/// <summary>
/// Sailor-relevant point of interest pulled from OpenStreetMap via the
/// Overpass API. Each entry is one OSM element (node, way, or relation
/// with a center) classified by the C# parser into exactly one
/// <see cref="MarinePoiCategory"/> from its tags.
///
/// <para>Stored in <see cref="OnaPlotter.Services.Pois.MarinePoiCache"/>
/// keyed by <see cref="Id"/>. The id is "n123", "w456", "r789" so a
/// node and a way that happen to share a numeric OSM id can coexist
/// in the same dictionary.</para>
///
/// <para>Geometry is collapsed to a single point: <c>out center</c>
/// in the Overpass query gives ways/relations a synthetic centre, and
/// nodes are points to begin with. Helms care "is the marina near me",
/// not the polygon outline -- a marker on the centre is good enough
/// for a chartplotter overlay.</para>
/// </summary>
/// <param name="Id">"n{osmId}", "w{osmId}", "r{osmId}". Stable across
/// fetches so the cache can de-duplicate when the same POI appears in
/// overlapping bbox queries during pan.</param>
/// <param name="Lat">WGS-84 latitude.</param>
/// <param name="Lon">WGS-84 longitude.</param>
/// <param name="Category">Resolved category. The parser picks the most
/// specific match when an element carries tags for more than one
/// category (e.g. a marina that is also a harbour collapses to
/// Marina).</param>
/// <param name="Name">OSM <c>name</c> tag if set, else null. Many
/// nodes carry the tag; ways/relations almost always do.</param>
/// <param name="Tags">Compact subset of OSM tags useful for the
/// popup: name, opening_hours, website, phone, the seamark:* hierarchy,
/// shop, amenity. Parser drops the rest so localStorage doesn't fill
/// with editor-only metadata (uid, source, addr:* etc.).</param>
/// <param name="LastSeenUtc">When the cache last saw this POI in a
/// fetch response. Drives FIFO eviction once the cache fills past its
/// cap; also tells the offline path "this was last refreshed N days
/// ago" for a future stale-data badge.</param>
public sealed record MarinePoi(
    string Id,
    double Lat,
    double Lon,
    MarinePoiCategory Category,
    string? Name,
    [property: JsonPropertyName("tags")] IReadOnlyDictionary<string, string> Tags,
    DateTime LastSeenUtc);
