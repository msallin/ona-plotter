using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Resources;

namespace OnaPlotter.Tests.Services.Resources;

/// <summary>
/// Pin the per-DTO content-equality functions used by ResourceStore to
/// dedup Changed events on reconnect-edge reconcile. A regression here
/// (e.g. forgetting to compare a newly-added field) lets a real
/// content change ride through as "unchanged" and the page-side drain
/// silently drops the redraw - the visible symptom is a stale chart
/// after a route edit on another plotter.
/// </summary>
public class ResourceContentEqualityTests
{
    // --- Routes ------------------------------------------------------

    [Test]
    public async Task RouteEquals_Same_Content_Returns_True()
    {
        var a = MakeRoute("r1", "Alpha", "[[0,0],[1,1]]");
        var b = MakeRoute("r1", "Alpha", "[[0,0],[1,1]]");
        await Assert.That(ResourceContentEquality.RouteEquals(a, b)).IsTrue();
    }

    [Test]
    public async Task RouteEquals_Different_Coordinates_Returns_False()
    {
        // Geometry change is the most common diff in the field
        // (helm dragging a waypoint on a route). Must not be missed.
        var a = MakeRoute("r1", "Alpha", "[[0,0],[1,1]]");
        var b = MakeRoute("r1", "Alpha", "[[0,0],[2,2]]");
        await Assert.That(ResourceContentEquality.RouteEquals(a, b)).IsFalse();
    }

    [Test]
    public async Task RouteEquals_Different_Name_Returns_False()
    {
        var a = MakeRoute("r1", "Alpha", "[[0,0]]");
        var b = MakeRoute("r1", "Bravo", "[[0,0]]");
        await Assert.That(ResourceContentEquality.RouteEquals(a, b)).IsFalse();
    }

    [Test]
    public async Task RouteEquals_Different_Distance_Returns_False()
    {
        var a = MakeRoute("r1", "Alpha", "[[0,0]]", distance: 100.0);
        var b = MakeRoute("r1", "Alpha", "[[0,0]]", distance: 200.0);
        await Assert.That(ResourceContentEquality.RouteEquals(a, b)).IsFalse();
    }

    // --- Waypoints ---------------------------------------------------

    [Test]
    public async Task WaypointEquals_Same_Content_Returns_True()
    {
        var a = MakeWaypoint("w1", "Anchor", 47.5, 9.5);
        var b = MakeWaypoint("w1", "Anchor", 47.5, 9.5);
        await Assert.That(ResourceContentEquality.WaypointEquals(a, b)).IsTrue();
    }

    [Test]
    public async Task WaypointEquals_Different_Position_Returns_False()
    {
        var a = MakeWaypoint("w1", "Anchor", 47.5, 9.5);
        var b = MakeWaypoint("w1", "Anchor", 47.6, 9.5);
        await Assert.That(ResourceContentEquality.WaypointEquals(a, b)).IsFalse();
    }

    [Test]
    public async Task WaypointEquals_Different_Description_Returns_False()
    {
        var a = MakeWaypoint("w1", "Anchor", 47.5, 9.5);
        a.Description = "5m chain";
        var b = MakeWaypoint("w1", "Anchor", 47.5, 9.5);
        b.Description = "10m chain";
        await Assert.That(ResourceContentEquality.WaypointEquals(a, b)).IsFalse();
    }

    // --- Notes -------------------------------------------------------

    [Test]
    public async Task NoteEquals_Same_Content_Returns_True()
    {
        var a = new SignalkNote
        {
            Id = "n1",
            Title = "Anchorage",
            Description = "Sandy bottom",
            Position = new NotePosition { Latitude = 47.5, Longitude = 9.5 },
        };
        var b = new SignalkNote
        {
            Id = "n1",
            Title = "Anchorage",
            Description = "Sandy bottom",
            Position = new NotePosition { Latitude = 47.5, Longitude = 9.5 },
        };
        await Assert.That(ResourceContentEquality.NoteEquals(a, b)).IsTrue();
    }

    [Test]
    public async Task NoteEquals_Different_Description_Returns_False()
    {
        var a = new SignalkNote
        {
            Id = "n1",
            Title = "Anchorage",
            Description = "Sandy bottom",
            Position = new NotePosition { Latitude = 47.5, Longitude = 9.5 },
        };
        var b = new SignalkNote
        {
            Id = "n1",
            Title = "Anchorage",
            Description = "Rocky bottom",
            Position = new NotePosition { Latitude = 47.5, Longitude = 9.5 },
        };
        await Assert.That(ResourceContentEquality.NoteEquals(a, b)).IsFalse();
    }

    // --- Regions -----------------------------------------------------

    [Test]
    public async Task RegionEquals_Same_Content_Returns_True()
    {
        var a = MakeRegion("rg1", "ZoneA", isHazard: true);
        var b = MakeRegion("rg1", "ZoneA", isHazard: true);
        await Assert.That(ResourceContentEquality.RegionEquals(a, b)).IsTrue();
    }

    [Test]
    public async Task RegionEquals_Different_IsHazard_Returns_False()
    {
        // The HazardousRegionAlarmRule depends on this flag; a change
        // here MUST refire Changed so the alarm rule re-evaluates.
        var a = MakeRegion("rg1", "ZoneA", isHazard: false);
        var b = MakeRegion("rg1", "ZoneA", isHazard: true);
        await Assert.That(ResourceContentEquality.RegionEquals(a, b)).IsFalse();
    }

    [Test]
    public async Task RegionEquals_Different_OuterRings_Returns_False()
    {
        var a = MakeRegion("rg1", "ZoneA", isHazard: false);
        a.OuterRings = new[] { new[] { new[] { 47.0, 9.0 }, new[] { 47.1, 9.1 } } };
        var b = MakeRegion("rg1", "ZoneA", isHazard: false);
        b.OuterRings = new[] { new[] { new[] { 47.0, 9.0 }, new[] { 48.0, 9.0 } } };
        await Assert.That(ResourceContentEquality.RegionEquals(a, b)).IsFalse();
    }

    [Test]
    public async Task RegionEquals_OuterRings_Same_Reference_Returns_True()
    {
        // Defensive: the reference-equal fast-path inside OuterRingsEqual
        // must not regress (covers the common case where ResourceStore
        // forwards the same DTO instance back through Replace - rare in
        // practice but cheap to verify).
        var rings = new[] { new[] { new[] { 47.0, 9.0 }, new[] { 47.1, 9.1 } } };
        var a = MakeRegion("rg1", "ZoneA", isHazard: false);
        a.OuterRings = rings;
        var b = MakeRegion("rg1", "ZoneA", isHazard: false);
        b.OuterRings = rings;
        await Assert.That(ResourceContentEquality.RegionEquals(a, b)).IsTrue();
    }

    // --- Helpers -----------------------------------------------------

    private static SignalkRoute MakeRoute(
        string id, string name, string coordinatesJson, double? distance = null)
    {
        using var doc = JsonDocument.Parse(coordinatesJson);
        return new SignalkRoute
        {
            Id = id,
            Name = name,
            Distance = distance,
            Feature = new GeoJsonFeature
            {
                Type = "Feature",
                Geometry = new GeoJsonGeometry
                {
                    Type = "LineString",
                    Coordinates = doc.RootElement.Clone(),
                },
            },
        };
    }

    private static SignalkWaypoint MakeWaypoint(
        string id, string name, double lat, double lon)
    {
        return new SignalkWaypoint
        {
            Id = id,
            Name = name,
            Latitude = lat,
            Longitude = lon,
        };
    }

    private static SignalkRegion MakeRegion(string id, string name, bool isHazard)
    {
        return new SignalkRegion
        {
            Id = id,
            Name = name,
            IsHazard = isHazard,
        };
    }
}
