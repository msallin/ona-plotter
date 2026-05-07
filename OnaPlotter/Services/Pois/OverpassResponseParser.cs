using OnaPlotter.Models;

namespace OnaPlotter.Services.Pois;

/// <summary>
/// Pure parser turning an <see cref="OverpassResponse"/> into a
/// <see cref="MarinePoi"/> array. No I/O; classification is a switch
/// on tag values. Lives next to <see cref="OverpassQueryBuilder"/> so
/// the two stay tightly co-evolved -- a tag added to the query that
/// isn't classified here would silently drop matches.
///
/// <para>One element produces zero or one POI: zero when the element
/// lacks a coordinate (a way without <c>out center</c> -- shouldn't
/// happen given the query, defensive against schema drift) or its
/// tags don't match any category; one otherwise. Categories are not
/// mutually exclusive in OSM (a marina that's also a harbour), so the
/// classifier picks the most specific match in priority order.</para>
/// </summary>
public static class OverpassResponseParser
{
    /// <summary>
    /// Walk every element, extract a POI when classification + coords
    /// succeed. Caller passes <paramref name="now"/> so tests can pin
    /// the LastSeenUtc; production wires through <see cref="TimeProvider"/>.
    /// </summary>
    public static IReadOnlyList<MarinePoi> Parse(OverpassResponse? response, DateTime now)
    {
        if (response?.Elements is null || response.Elements.Length == 0) return [];
        var pois = new List<MarinePoi>(response.Elements.Length);
        foreach (var el in response.Elements)
        {
            var poi = TryMapElement(el, now);
            if (poi is not null) pois.Add(poi);
        }
        return pois;
    }

    /// <summary>Classify by tags + extract coordinates. Returns null
    /// when the element is unmappable.</summary>
    internal static MarinePoi? TryMapElement(OverpassElement? el, DateTime now)
    {
        if (el is null) return null;
        if (el.Id == 0) return null;

        // Coordinate: nodes have direct lat/lon; ways/relations have
        // them under .center (because the query uses `out center`).
        // Either path is acceptable; the helm renders a marker either
        // way and doesn't see the difference.
        double? lat = el.Lat ?? el.Center?.Lat;
        double? lon = el.Lon ?? el.Center?.Lon;
        if (lat is null || lon is null) return null;
        if (!double.IsFinite(lat.Value) || !double.IsFinite(lon.Value)) return null;
        if (lat.Value < -90 || lat.Value > 90) return null;
        if (lon.Value < -180 || lon.Value > 180) return null;

        if (el.Tags is null || el.Tags.Count == 0) return null;
        var category = ClassifyTags(el.Tags);
        if (category is null) return null;

        var typePrefix = el.Type switch
        {
            "node" => "n",
            "way" => "w",
            "relation" => "r",
            _ => null,
        };
        if (typePrefix is null) return null;

        var id = typePrefix + el.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var name = el.Tags.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n)
            ? n
            : null;

        return new MarinePoi(
            Id: id,
            Lat: lat.Value,
            Lon: lon.Value,
            Category: category.Value,
            Name: name,
            Tags: ProjectPopupTags(el.Tags),
            LastSeenUtc: now);
    }

    /// <summary>
    /// Pick the category for a tag dict. Priority order is
    /// most-specific-first so an OSM element that carries multiple
    /// matching tags collapses to the more informative label
    /// (a marina that also has <c>harbour=yes</c> renders as Marina,
    /// not Harbour). Returns null when no category matches; the
    /// caller drops the element.
    /// </summary>
    internal static MarinePoiCategory? ClassifyTags(IReadOnlyDictionary<string, string> tags)
    {
        // Pump-out: rare and specific; check first so a pump-out tagged
        // on a marina doesn't lose its identity.
        if (tags.TryGetValue("waste_disposal", out var wd) && wd == "marine") return MarinePoiCategory.PumpOut;
        if (tags.TryGetValue("pumpout", out var po) && po == "yes") return MarinePoiCategory.PumpOut;

        // Fuel: query already filtered on boat=yes. Defence-in-depth
        // re-check so a future query change can't accidentally surface
        // road petrol stations.
        if (tags.TryGetValue("amenity", out var amen) && amen == "fuel"
            && tags.TryGetValue("boat", out var boat) && boat == "yes")
        {
            return MarinePoiCategory.Fuel;
        }

        // Chandlery: the two recognised shop= values.
        if (tags.TryGetValue("shop", out var shop)
            && (shop == "boat" || shop == "ship_chandler"))
        {
            return MarinePoiCategory.Chandlery;
        }

        // Slipway: leisure=slipway is unambiguous.
        if (tags.TryGetValue("leisure", out var leis))
        {
            if (leis == "slipway") return MarinePoiCategory.Slipway;
            if (leis == "marina") return MarinePoiCategory.Marina;
        }

        // Harbour: harbour=yes is the canonical OSM tag; the seamark
        // variant is what nautical chart tooling tends to write.
        if (tags.TryGetValue("harbour", out var harb) && harb == "yes") return MarinePoiCategory.Harbour;
        if (tags.TryGetValue("seamark:type", out var seamark))
        {
            if (seamark == "harbour") return MarinePoiCategory.Harbour;
            // Anything that starts with "mooring" -- mooring,
            // mooring_buoy, mooring_anchorage etc. The query uses
            // a regex match so the parser must too.
            if (seamark.StartsWith("mooring", StringComparison.Ordinal)) return MarinePoiCategory.Mooring;
        }

        // Mooring: explicit mooring=yes. Checked after the seamark
        // path so a node that's both seamark:type=mooring_buoy and
        // mooring=yes lands the same way regardless of which was
        // serialised first.
        if (tags.TryGetValue("mooring", out var moor) && moor == "yes") return MarinePoiCategory.Mooring;

        // Pier: man_made=pier is broad (commercial, fishing, tourist).
        // Keep last among the harbour-area structures so an element
        // with both man_made=pier AND leisure=marina (a marina built
        // on a pier) still classifies as Marina.
        if (tags.TryGetValue("man_made", out var mm) && mm == "pier") return MarinePoiCategory.Pier;

        // Drinking water: lowest priority because it's the broadest
        // tag (every park tap), so anything more specific wins first.
        if (amen == "drinking_water") return MarinePoiCategory.DrinkingWater;

        return null;
    }

    /// <summary>
    /// Subset of OSM tags we keep for the popup. Drops editor-only
    /// metadata (uid, source, addr:* unless useful) so the
    /// localStorage cache stays compact.
    /// <para>OSM tag schema documentation:
    /// <c>https://wiki.openstreetmap.org/wiki/Map_features</c>.</para>
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ProjectPopupTags(IReadOnlyDictionary<string, string> tags)
    {
        var keep = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in PopupTagKeys)
        {
            if (tags.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                keep[key] = v;
            }
        }
        // seamark:* sub-tags carry per-feature detail (mooring number,
        // depth, water access) that's useful in the popup but doesn't
        // fit in a hand-curated whitelist. Walk every key for the
        // prefix and keep the lot. Cap at 8 entries so a malformed
        // OSM element with 200 seamark sub-tags can't blow up the
        // localStorage entry. The existing keys above (e.g.
        // seamark:type) won't double-add because Dictionary.set just
        // overwrites with the same value.
        int seamarkCount = 0;
        foreach (var (k, v) in tags)
        {
            if (k.StartsWith("seamark:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(v))
            {
                if (++seamarkCount > 8) break;
                keep[k] = v;
            }
        }
        return keep;
    }

    /// <summary>
    /// Whitelist of OSM tags we surface in the popup. Stable list so
    /// the cached entries' shape stays predictable across versions;
    /// adding a key here is a localStorage-cache schema bump (bump
    /// MarinePoiCache.StorageKey to v2).
    /// </summary>
    private static readonly string[] PopupTagKeys =
    [
        "name",
        "name:en",
        "operator",
        "opening_hours",
        "website",
        "contact:website",
        "phone",
        "contact:phone",
        "fee",
        "amenity",
        "leisure",
        "shop",
        "man_made",
        "harbour",
        "mooring",
        "boat",
        "fuel:diesel",
        "fuel:petrol",
        "vhf_channel",
        "capacity",
        "depth",
    ];
}
