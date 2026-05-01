using OnaPlotter.Models;
using OnaPlotter.Services.Js;
using OnaPlotter.Services.Map;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the marker batch + visibility toggle facade for waypoints,
/// notes, and regions.
/// </summary>
public class MapResourceControllerTests
{
    private sealed class FakeResourceJs : IMapResourceJs
    {
        public List<(string id, double? lat, double? lon, string? name)> Waypoints { get; } = [];
        public List<string> WaypointRemoves { get; } = [];
        public List<(string id, double lat, double lon, string? title, string? desc, string? createdAtIso)> Notes { get; } = [];
        public List<string> NoteRemoves { get; } = [];
        public int NoteClears { get; private set; }
        public List<string> NoteOpens { get; } = [];
        public List<(string id, IReadOnlyList<double[][]> rings, string? title, string? desc)> Regions { get; } = [];
        public List<string> RegionRemoves { get; } = [];
        public int RegionClears { get; private set; }
        public List<(string id, double[][] ring)> RegionFocus { get; } = [];
        public List<(double lat, double lon, double r)> CirclePreviews { get; } = [];
        public int CirclePreviewClears { get; private set; }

        public Task AddWaypointMarkerAsync(string id, double? lat, double? lon, string? name)
        {
            Waypoints.Add((id, lat, lon, name));
            return Task.CompletedTask;
        }

        public Task RemoveWaypointMarkerAsync(string id)
        {
            WaypointRemoves.Add(id);
            return Task.CompletedTask;
        }

        public Task AddNoteMarkerAsync(string id, double lat, double lon, string? title, string? description, string? createdAtIso)
        {
            Notes.Add((id, lat, lon, title, description, createdAtIso));
            return Task.CompletedTask;
        }

        public Task RemoveNoteMarkerAsync(string id)
        {
            NoteRemoves.Add(id);
            return Task.CompletedTask;
        }

        public Task ClearNotesAsync() { NoteClears++; return Task.CompletedTask; }
        public Task OpenNotePopupAsync(string id) { NoteOpens.Add(id); return Task.CompletedTask; }

        public Task AddRegionAsync(string id, IReadOnlyList<double[][]> rings, string? title, string? description)
        {
            Regions.Add((id, rings, title, description));
            return Task.CompletedTask;
        }

        public Task RemoveRegionAsync(string id)
        {
            RegionRemoves.Add(id);
            return Task.CompletedTask;
        }

        public Task ClearRegionsAsync() { RegionClears++; return Task.CompletedTask; }

        public Task FocusRegionAsync(string id, double[][] firstRing)
        {
            RegionFocus.Add((id, firstRing));
            return Task.CompletedTask;
        }

        public Task SetCirclePreviewAsync(double lat, double lon, double radiusMeters)
        {
            CirclePreviews.Add((lat, lon, radiusMeters));
            return Task.CompletedTask;
        }

        public Task ClearCirclePreviewAsync() { CirclePreviewClears++; return Task.CompletedTask; }
    }

    [Test]
    public async Task DrawAll_Pushes_Each_Resource_Type()
    {
        var js = new FakeResourceJs();
        var ctrl = new MapResourceController(js);
        var waypoints = new[]
        {
            new SignalkWaypoint { Id = "w1", Name = "WP1", Latitude = 47.0, Longitude = 8.0 },
        };
        var notes = new[]
        {
            new SignalkNote { Id = "n1", Title = "Note", Position = new NotePosition { Latitude = 47.5, Longitude = 8.5 } },
        };
        var regions = new[]
        {
            new SignalkRegion { Id = "r1", Name = "Reg" },
        };

        await ctrl.DrawAllAsync(waypoints, notes, regions);

        await Assert.That(js.Waypoints.Count).IsEqualTo(1);
        await Assert.That(js.Notes.Count).IsEqualTo(1);
        await Assert.That(js.Regions.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DrawAll_Skips_Waypoints_Without_Position()
    {
        // Defensive: GetAllAsync usually filters but we re-check.
        var js = new FakeResourceJs();
        var ctrl = new MapResourceController(js);
        var waypoints = new[]
        {
            new SignalkWaypoint { Id = "w1", Name = "no pos" },
            new SignalkWaypoint { Id = "w2", Latitude = 47.0, Longitude = 8.0 },
        };

        await ctrl.DrawAllAsync(waypoints, [], []);

        await Assert.That(js.Waypoints.Count).IsEqualTo(1);
        await Assert.That(js.Waypoints[0].id).IsEqualTo("w2");
    }

    [Test]
    public async Task SetNotesVisible_True_Pushes_Markers()
    {
        var js = new FakeResourceJs();
        var ctrl = new MapResourceController(js);
        var notes = new[]
        {
            new SignalkNote { Id = "n1", Title = "A", Position = new NotePosition { Latitude = 1, Longitude = 2 } },
            new SignalkNote { Id = "n2", Title = "B", Position = new NotePosition { Latitude = 3, Longitude = 4 } },
        };

        await ctrl.SetNotesVisibleAsync(true, notes);

        await Assert.That(js.Notes.Count).IsEqualTo(2);
        await Assert.That(ctrl.NotesVisible).IsTrue();
    }

    [Test]
    public async Task SetNotesVisible_False_Calls_Clear()
    {
        var js = new FakeResourceJs();
        var ctrl = new MapResourceController(js);

        await ctrl.SetNotesVisibleAsync(false, []);

        await Assert.That(js.NoteClears).IsEqualTo(1);
        await Assert.That(ctrl.NotesVisible).IsFalse();
    }

    [Test]
    public async Task SetRegionsVisible_True_Pushes_Polygons()
    {
        var js = new FakeResourceJs();
        var ctrl = new MapResourceController(js);
        var regions = new[]
        {
            new SignalkRegion { Id = "r1", Name = "A" },
            new SignalkRegion { Id = "r2", Name = "B" },
        };

        await ctrl.SetRegionsVisibleAsync(true, regions);

        await Assert.That(js.Regions.Count).IsEqualTo(2);
        await Assert.That(ctrl.RegionsVisible).IsTrue();
    }

    [Test]
    public async Task SetRegionsVisible_False_Calls_Clear()
    {
        var js = new FakeResourceJs();
        var ctrl = new MapResourceController(js);

        await ctrl.SetRegionsVisibleAsync(false, []);

        await Assert.That(js.RegionClears).IsEqualTo(1);
        await Assert.That(ctrl.RegionsVisible).IsFalse();
    }
}
