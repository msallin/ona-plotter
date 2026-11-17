using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Api;

/// <summary>
/// Turns a deserialised SignalK resource into the shape the helm-facing
/// surface consumes. The REST clients (<c>*Api.GetAllAsync</c>) and the
/// WebSocket delta handlers in <c>ResourceStore</c> both go through
/// these, so a resource looks the same whichever path delivered it.
/// Each method returns null when the resource can't be rendered and
/// must be dropped.
/// </summary>
internal static class ResourceNormalizer
{
    /// <summary>Stamps the id; only LineString routes are rendered.</summary>
    public static SignalkRoute? Route(string id, SignalkRoute route)
    {
        route.Id = id;
        return route.Feature?.Geometry?.Type is "LineString" ? route : null;
    }

    /// <summary>Stamps the id and lifts the point coordinates,
    /// description and MOB metadata onto the flat projection fields.</summary>
    public static SignalkWaypoint Waypoint(string id, SignalkWaypoint wp)
    {
        wp.Id = id;
        if (wp.Feature?.Geometry?.Coordinates is { } coords
            && coords.ToLatLonPoint() is { } latLon)
        {
            wp.Latitude = latLon.Latitude;
            wp.Longitude = latLon.Longitude;
        }
        wp.LiftFromFeatureProperties();
        return wp;
    }

    /// <summary>Stamps the id; notes without a position (region or
    /// geohash references) aren't renderable and are dropped.</summary>
    public static SignalkNote? Note(string id, SignalkNote note)
    {
        if (note.Position is null) return null;
        note.Id = id;
        return note;
    }

    /// <summary>Stamps the id and fills <c>OuterRings</c> with
    /// Leaflet-ordered <c>[lat, lon]</c> rings (the wire carries GeoJSON
    /// <c>[lon, lat]</c>); regions with no renderable ring are
    /// dropped. Throws <see cref="InvalidDataException"/> for a region
    /// with an invalid vertex (see <see cref="RegionApi.ExtractOuterRings"/>);
    /// callers treat that as "reject this region", keeping any earlier
    /// version.</summary>
    public static SignalkRegion? Region(string id, SignalkRegion region)
    {
        region.Id = id;
        if (region.Feature?.Geometry is { Coordinates: { } coords } geometry)
            region.OuterRings = RegionApi.ExtractOuterRings(coords, geometry.Type);
        return region.OuterRings.Count == 0 ? null : region;
    }
}
