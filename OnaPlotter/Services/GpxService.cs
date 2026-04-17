using System.Globalization;
using System.Xml.Linq;
using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// GPX 1.1 XML import and export for routes and waypoints.
/// Stateless: all methods are static and pure.
/// Throws on malformed XML; the Razor caller wraps with a user-facing toast.
/// </summary>
public static class GpxService
{
    private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly NumberStyles NumStyle = NumberStyles.Float;

    /// <summary>
    /// Parses a GPX XML string and extracts routes and waypoints.
    /// Coordinates are returned as [lat, lon] pairs to match Leaflet's convention.
    /// </summary>
    public static GpxData Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root;
        if (root is null) return new GpxData([], []);

        var routes = new List<GpxRoute>();
        foreach (var rte in root.Descendants(Gpx + "rte"))
        {
            var name = rte.Element(Gpx + "name")?.Value ?? "Imported Route";
            var coords = new List<double[]>();
            foreach (var rtept in rte.Elements(Gpx + "rtept"))
            {
                if (TryReadLatLon(rtept, out double lat, out double lon))
                    coords.Add([lat, lon]);
            }
            if (coords.Count >= 2)
                routes.Add(new GpxRoute(name, [.. coords]));
        }

        var waypoints = new List<GpxWaypoint>();
        foreach (var wpt in root.Descendants(Gpx + "wpt"))
        {
            if (!TryReadLatLon(wpt, out double lat, out double lon)) continue;
            var name = wpt.Element(Gpx + "name")?.Value ?? "WPT";
            waypoints.Add(new GpxWaypoint(name, lat, lon));
        }

        return new GpxData(routes, waypoints);
    }

    /// <summary>
    /// Generates a GPX 1.1 XML string from SignalK routes and waypoints.
    /// Malformed route geometry is skipped silently (a single bad route must
    /// not prevent exporting the rest).
    /// </summary>
    public static string Export(IEnumerable<SignalkRoute> routes, IEnumerable<SignalkWaypoint> waypoints)
    {
        var gpx = new XElement(Gpx + "gpx",
            new XAttribute("version", "1.1"),
            new XAttribute("creator", "OnaPlotter"),
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"));

        foreach (var wp in waypoints)
            AppendWaypoint(gpx, wp);

        foreach (var route in routes)
            AppendRoute(gpx, route);

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), gpx);
        return doc.ToString();
    }

    private static void AppendWaypoint(XElement parent, SignalkWaypoint wp)
    {
        if (wp.Latitude is null || wp.Longitude is null) return;
        var wptEl = new XElement(Gpx + "wpt",
            new XAttribute("lat", wp.Latitude.Value.ToString("F6", Inv)),
            new XAttribute("lon", wp.Longitude.Value.ToString("F6", Inv)));
        if (wp.Name is not null)
            wptEl.Add(new XElement(Gpx + "name", wp.Name));
        parent.Add(wptEl);
    }

    private static void AppendRoute(XElement parent, SignalkRoute route)
    {
        if (route.Feature?.Geometry is null) return;
        var rteEl = new XElement(Gpx + "rte");
        if (route.Name is not null)
            rteEl.Add(new XElement(Gpx + "name", route.Name));

        foreach (var point in route.Feature.Geometry.Coordinates.EnumerateArray())
        {
            int i = 0;
            var arr = new double[2];
            foreach (var val in point.EnumerateArray())
            {
                if (i < 2) arr[i++] = val.GetDouble();
            }
            // GeoJSON [lon, lat] -> GPX lat, lon attributes.
            rteEl.Add(new XElement(Gpx + "rtept",
                new XAttribute("lat", arr[1].ToString("F6", Inv)),
                new XAttribute("lon", arr[0].ToString("F6", Inv))));
        }

        parent.Add(rteEl);
    }

    private static bool TryReadLatLon(XElement element, out double lat, out double lon)
    {
        lat = 0; lon = 0;
        return double.TryParse(element.Attribute("lat")?.Value, NumStyle, Inv, out lat)
            && double.TryParse(element.Attribute("lon")?.Value, NumStyle, Inv, out lon);
    }
}

public sealed record GpxData(List<GpxRoute> Routes, List<GpxWaypoint> Waypoints);
public sealed record GpxRoute(string Name, double[][] Coords);
public sealed record GpxWaypoint(string Name, double Lat, double Lon);
