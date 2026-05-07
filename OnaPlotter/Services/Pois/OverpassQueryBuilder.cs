using System.Globalization;
using System.Text;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Pois;

/// <summary>
/// Pure builder for the Overpass QL query string. Takes a bbox and a
/// set of enabled <see cref="MarinePoiCategory"/> values, emits a
/// single Overpass request that unions the matching tag filters and
/// returns each match's center + tags.
///
/// <para>Tested in isolation: no HTTP, no I/O, no allocations beyond
/// the StringBuilder. Lets <see cref="OverpassQueryBuilderTests"/>
/// pin the exact wire shape for each category combination so a
/// future tag-mapping change has to come through a test edit.</para>
///
/// <para>Bbox parameter order in Overpass QL is
/// <c>(south, west, north, east)</c>, NOT <c>(west, south, east, north)</c>
/// like Leaflet. The builder takes Leaflet-style on the way in and
/// re-orders inside; callers therefore pass bounds in the same shape
/// the JS side speaks.</para>
/// </summary>
public static class OverpassQueryBuilder
{
    /// <summary>Server-side timeout in seconds. The default Overpass
    /// timeout is 180 s; we cap at 25 s because a marine-POI query is
    /// small (a handful of tag filters in a bbox the helm just
    /// panned) and waiting longer than that is dead-time on the
    /// chart - the helm has already moved the viewport. Matches the
    /// per-call timeout below in <see cref="OverpassPoiService"/>.</summary>
    public const int ServerTimeoutSeconds = 25;

    /// <summary>Result-size cap (bytes). Public Overpass instances cap
    /// at 256 MB; we ask for 16 MB which is plenty for any practical
    /// chart bbox (a Greek archipelago zoomed-out worst-case is a few
    /// hundred KB).</summary>
    public const int MaxResultBytes = 16 * 1024 * 1024;

    /// <summary>Build the Overpass QL query for the given bbox and
    /// category set. Returns null when no categories are requested
    /// (caller should not fire an empty query against the public
    /// endpoint - it'd return everything that happens to match
    /// no filter, which is "nothing", but it still costs a round
    /// trip).</summary>
    /// <param name="south">Leaflet south bound.</param>
    /// <param name="west">Leaflet west bound.</param>
    /// <param name="north">Leaflet north bound.</param>
    /// <param name="east">Leaflet east bound.</param>
    /// <param name="categories">Enabled categories. Empty -> null
    /// return. Order doesn't affect the result; the builder uses a
    /// stable enum-order traversal so the same input always produces
    /// the same query (cache-key stability + test stability).</param>
    public static string? Build(
        double south, double west, double north, double east,
        IReadOnlySet<MarinePoiCategory> categories)
    {
        if (categories.Count == 0) return null;

        // Order-stable iteration so two callers passing the same set
        // always produce the same query string. The cache layer hashes
        // on the query string, and a stable ordering means a category
        // set rebuild (e.g. user toggles a checkbox off and on)
        // doesn't invalidate cache hits.
        var orderedCategories = Enum.GetValues<MarinePoiCategory>()
            .Where(categories.Contains)
            .ToArray();

        var bbox = string.Create(CultureInfo.InvariantCulture,
            $"{south:F6},{west:F6},{north:F6},{east:F6}");

        var sb = new StringBuilder(2048);
        sb.Append("[out:json][timeout:");
        sb.Append(ServerTimeoutSeconds);
        sb.Append("][maxsize:");
        sb.Append(MaxResultBytes);
        sb.Append("];(");
        foreach (var cat in orderedCategories)
        {
            AppendCategoryFilters(sb, cat, bbox);
        }
        // out center: ways/relations get a synthetic center point so the
        // marker code can render every match as a single L.marker.
        // tags: include the OSM tag dict on every element so the popup
        // can show name / opening_hours / etc.
        sb.Append(");out center tags;");
        return sb.ToString();
    }

    /// <summary>
    /// Emit the node + way (and where applicable, relation) filters
    /// for one category. The set of OSM tag combinations per category
    /// is documented in <see cref="MarinePoiCategory"/>'s xmldoc;
    /// changes here must update both that doc and
    /// <see cref="OverpassResponseParser.ClassifyTags"/>.
    /// </summary>
    private static void AppendCategoryFilters(StringBuilder sb, MarinePoiCategory cat, string bbox)
    {
        switch (cat)
        {
            case MarinePoiCategory.Fuel:
                // amenity=fuel + boat=yes is the OSM convention for
                // marine fuel docks. Without the boat=yes filter we'd
                // get every road petrol station within the bbox; with
                // it, mis-tagged marine docks are missed instead. v1
                // takes the precision-over-recall side; helms can
                // always cross-check on NFL for the missing ones.
                sb.Append("node[\"amenity\"=\"fuel\"][\"boat\"=\"yes\"](").Append(bbox).Append(");");
                sb.Append("way[\"amenity\"=\"fuel\"][\"boat\"=\"yes\"](").Append(bbox).Append(");");
                break;

            case MarinePoiCategory.Marina:
                sb.Append("node[\"leisure\"=\"marina\"](").Append(bbox).Append(");");
                sb.Append("way[\"leisure\"=\"marina\"](").Append(bbox).Append(");");
                sb.Append("relation[\"leisure\"=\"marina\"](").Append(bbox).Append(");");
                break;

            case MarinePoiCategory.Harbour:
                sb.Append("node[\"harbour\"=\"yes\"](").Append(bbox).Append(");");
                sb.Append("way[\"harbour\"=\"yes\"](").Append(bbox).Append(");");
                sb.Append("node[\"seamark:type\"=\"harbour\"](").Append(bbox).Append(");");
                sb.Append("way[\"seamark:type\"=\"harbour\"](").Append(bbox).Append(");");
                break;

            case MarinePoiCategory.Mooring:
                sb.Append("node[\"mooring\"=\"yes\"](").Append(bbox).Append(");");
                sb.Append("way[\"mooring\"=\"yes\"](").Append(bbox).Append(");");
                // ~"^mooring" matches mooring, mooring_buoy, mooring_anchorage etc.
                sb.Append("node[\"seamark:type\"~\"^mooring\"](").Append(bbox).Append(");");
                break;

            case MarinePoiCategory.Slipway:
                sb.Append("node[\"leisure\"=\"slipway\"](").Append(bbox).Append(");");
                sb.Append("way[\"leisure\"=\"slipway\"](").Append(bbox).Append(");");
                break;

            case MarinePoiCategory.Pier:
                sb.Append("node[\"man_made\"=\"pier\"](").Append(bbox).Append(");");
                sb.Append("way[\"man_made\"=\"pier\"](").Append(bbox).Append(");");
                break;

            case MarinePoiCategory.Chandlery:
                sb.Append("node[\"shop\"=\"boat\"](").Append(bbox).Append(");");
                sb.Append("way[\"shop\"=\"boat\"](").Append(bbox).Append(");");
                sb.Append("node[\"shop\"=\"ship_chandler\"](").Append(bbox).Append(");");
                sb.Append("way[\"shop\"=\"ship_chandler\"](").Append(bbox).Append(");");
                break;

            case MarinePoiCategory.DrinkingWater:
                sb.Append("node[\"amenity\"=\"drinking_water\"](").Append(bbox).Append(");");
                sb.Append("way[\"amenity\"=\"drinking_water\"](").Append(bbox).Append(");");
                break;

            case MarinePoiCategory.PumpOut:
                sb.Append("node[\"waste_disposal\"=\"marine\"](").Append(bbox).Append(");");
                sb.Append("way[\"waste_disposal\"=\"marine\"](").Append(bbox).Append(");");
                sb.Append("node[\"pumpout\"=\"yes\"](").Append(bbox).Append(");");
                sb.Append("way[\"pumpout\"=\"yes\"](").Append(bbox).Append(");");
                break;
        }
    }
}
