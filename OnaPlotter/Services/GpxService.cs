// GPX import/export service.
// Parses GPX XML (routes, waypoints) for import to SignalK,
// and generates GPX XML from SignalK resources for export.

using System.Xml.Linq;
using OnaPlotter.Models;

namespace OnaPlotter.Services;

public sealed class GpxService
{
    private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";

    /// <summary>
    /// Parses a GPX XML string and extracts routes and waypoints.
    /// Routes are returned as (name, [[lat,lon],...]) tuples.
    /// Waypoints are returned as (name, lat, lon) tuples.
    /// </summary>
    public static GpxData Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root;
        if (root is null) return new GpxData([], []);

        // Extract routes (<rte> elements).
        var routes = new List<GpxRoute>();
        foreach (var rte in root.Descendants(Gpx + "rte"))
        {
            string? name = rte.Element(Gpx + "name")?.Value;
            var coords = new List<double[]>();
            foreach (var rtept in rte.Elements(Gpx + "rtept"))
            {
                if (double.TryParse(rtept.Attribute("lat")?.Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double lat)
                    && double.TryParse(rtept.Attribute("lon")?.Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double lon))
                {
                    coords.Add([lat, lon]);
                }
            }
            if (coords.Count >= 2)
                routes.Add(new GpxRoute(name ?? "Imported Route", [.. coords]));
        }

        // Extract waypoints (<wpt> elements).
        var waypoints = new List<GpxWaypoint>();
        foreach (var wpt in root.Descendants(Gpx + "wpt"))
        {
            string? name = wpt.Element(Gpx + "name")?.Value;
            if (double.TryParse(wpt.Attribute("lat")?.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double lat)
                && double.TryParse(wpt.Attribute("lon")?.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double lon))
            {
                waypoints.Add(new GpxWaypoint(name ?? "WPT", lat, lon));
            }
        }

        return new GpxData(routes, waypoints);
    }

    /// <summary>
    /// Generates a GPX XML string from routes and waypoints.
    /// Coordinates are in [lat, lon] format.
    /// </summary>
    public static string Export(IEnumerable<SignalkRoute> routes, IEnumerable<SignalkWaypoint> waypoints)
    {
        var gpx = new XElement(Gpx + "gpx",
            new XAttribute("version", "1.1"),
            new XAttribute("creator", "OnaPlotter"),
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"));

        // Waypoints.
        foreach (var wp in waypoints)
        {
            if (wp.Latitude is null || wp.Longitude is null) continue;
            var wptEl = new XElement(Gpx + "wpt",
                new XAttribute("lat", wp.Latitude.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)),
                new XAttribute("lon", wp.Longitude.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)));
            if (wp.Name is not null)
                wptEl.Add(new XElement(Gpx + "name", wp.Name));
            gpx.Add(wptEl);
        }

        // Routes.
        foreach (var route in routes)
        {
            if (route.Feature?.Geometry is null) continue;
            var rteEl = new XElement(Gpx + "rte");
            if (route.Name is not null)
                rteEl.Add(new XElement(Gpx + "name", route.Name));

            try
            {
                foreach (var point in route.Feature.Geometry.Coordinates.EnumerateArray())
                {
                    var arr = new double[2];
                    int i = 0;
                    foreach (var val in point.EnumerateArray())
                    {
                        if (i < 2) arr[i++] = val.GetDouble();
                    }
                    // GeoJSON [lon, lat] -> GPX lat, lon attributes.
                    rteEl.Add(new XElement(Gpx + "rtept",
                        new XAttribute("lat", arr[1].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)),
                        new XAttribute("lon", arr[0].ToString("F6", System.Globalization.CultureInfo.InvariantCulture))));
                }
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"GPX export: failed to serialize route '{route.Name}': {ex.Message}");
            }

            gpx.Add(rteEl);
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), gpx);
        return doc.ToString();
    }
}

public sealed record GpxData(List<GpxRoute> Routes, List<GpxWaypoint> Waypoints);
public sealed record GpxRoute(string Name, double[][] Coords);
public sealed record GpxWaypoint(string Name, double Lat, double Lon);
