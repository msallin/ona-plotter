using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Places;

namespace OnaPlotter.Tests.Services.Places;

/// <summary>
/// Pins the own-data substring index. The mappers (TryMapWaypoint /
/// TryMapNote / TryMapRegion) are static + pure, so most coverage
/// goes into them. The lazy-load + TTL semantics get a couple of
/// targeted tests with a FakeTimeProvider so we can advance past
/// StaleTtl without sleeping.
/// </summary>
public class OwnPlacesIndexTests
{
    [Test]
    public async Task TryMapWaypoint_Maps_Well_Formed()
    {
        var w = new SignalkWaypoint
        {
            Id = "wpt1",
            Name = "Anchorage Cay",
            Latitude = 24.7,
            Longitude = -81.1,
        };
        var r = OwnPlacesIndex.TryMapWaypoint(w);
        await Assert.That(r).IsNotNull();
        await Assert.That(r!.Source).IsEqualTo("waypoint");
        await Assert.That(r.Lat).IsEqualTo(24.7);
        await Assert.That(r.Lon).IsEqualTo(-81.1);
    }

    [Test]
    public async Task TryMapWaypoint_Null_When_Name_Or_Coords_Missing()
    {
        var noName = new SignalkWaypoint { Id = "x", Latitude = 0, Longitude = 0 };
        await Assert.That(OwnPlacesIndex.TryMapWaypoint(noName)).IsNull();

        var noLat = new SignalkWaypoint { Id = "x", Name = "Foo", Longitude = 0 };
        await Assert.That(OwnPlacesIndex.TryMapWaypoint(noLat)).IsNull();
    }

    [Test]
    public async Task TryMapNote_Maps_Title_And_Position()
    {
        var n = new SignalkNote
        {
            Id = "n1",
            Title = "Watch the rocks",
            Position = new NotePosition { Latitude = 47.4, Longitude = 8.5 },
        };
        var r = OwnPlacesIndex.TryMapNote(n);
        await Assert.That(r).IsNotNull();
        await Assert.That(r!.Source).IsEqualTo("note");
        await Assert.That(r.Name).IsEqualTo("Watch the rocks");
    }

    [Test]
    public async Task TryMapNote_Null_When_No_Position()
    {
        var n = new SignalkNote { Id = "n", Title = "Floating note" };
        await Assert.That(OwnPlacesIndex.TryMapNote(n)).IsNull();
    }

    [Test]
    public async Task TryMapRegion_Centroids_The_First_Outer_Ring()
    {
        // Square from (0,0) to (10,10), expected centroid (5, 5).
        // OuterRings stores [lat, lon] (Leaflet order); centroid math
        // averages both columns.
        var r = new SignalkRegion
        {
            Id = "rg",
            Name = "Restricted",
            OuterRings = new List<double[][]>
            {
                new[]
                {
                    new[] { 0.0,  0.0 },
                    new[] { 0.0,  10.0 },
                    new[] { 10.0, 10.0 },
                    new[] { 10.0, 0.0 },
                },
            },
        };
        var mapped = OwnPlacesIndex.TryMapRegion(r);
        await Assert.That(mapped).IsNotNull();
        await Assert.That(mapped!.Lat).IsEqualTo(5.0);
        await Assert.That(mapped.Lon).IsEqualTo(5.0);
        await Assert.That(mapped.Source).IsEqualTo("region");
    }

    [Test]
    public async Task TryMapRegion_Null_When_No_Outer_Rings()
    {
        var r = new SignalkRegion { Id = "rg", Name = "Empty" };
        await Assert.That(OwnPlacesIndex.TryMapRegion(r)).IsNull();
    }

    private sealed class FakeWaypointApi : IWaypointApi
    {
        public List<SignalkWaypoint> Waypoints { get; } = [];
        public int LoadCount { get; private set; }
        public Task<List<SignalkWaypoint>> GetAllAsync(CancellationToken ct = default)
        { LoadCount++; return Task.FromResult(Waypoints.ToList()); }
        public Task<ApiResult<string>> CreateAsync(string name, double lat, double lon, string? description = null, CancellationToken ct = default)
            => Task.FromResult(ApiResult<string>.Ok(""));
        public Task<ApiResult<string>> CreateAsync(string name, double lat, double lon, string? description,
            bool? isMob, bool? isActive, string? mobAlarmId, CancellationToken ct = default)
            => Task.FromResult(ApiResult<string>.Ok(""));
        public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> UpdateAsync(SignalkWaypoint waypoint, string newName, string? newDescription = null, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> UpdateAsync(SignalkWaypoint waypoint, string newName, string? newDescription,
            bool? isMob, bool? isActive, string? mobAlarmId, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> PutWithIdAsync(string id, string name, double lat, double lon,
            string? description, bool? isMob, bool? isActive, string? mobAlarmId,
            CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
    }

    private sealed class FakeNoteApi : INoteApi
    {
        public Task<List<SignalkNote>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<SignalkNote>());
        public Task<ApiResult<string>> CreateAsync(string title, string description, double lat, double lon, CancellationToken ct = default) =>
            Task.FromResult(ApiResult<string>.Ok(""));
        public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> UpdateAsync(SignalkNote n, string newTitle, string? newDescription = null, CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);
    }

    private sealed class FakeRegionApi : IRegionApi
    {
        public Task<List<SignalkRegion>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<SignalkRegion>());
        public Task<ApiResult<string>> CreateCircleAsync(string name, string description, double lat, double lon, double radiusMeters, bool isHazard = false, CancellationToken ct = default) =>
            Task.FromResult(ApiResult<string>.Ok(""));
        public Task<ApiResult<string>> CreatePolygonAsync(string name, string description, double[][] vertices, bool isHazard = false, CancellationToken ct = default) =>
            Task.FromResult(ApiResult<string>.Ok(""));
        public Task<ApiResult> UpdatePolygonAsync(string id, string name, string description, double[][] vertices,
            bool isHazard = false, DateTime? createdAt = null,
            double? centerLat = null, double? centerLon = null, double? radiusMeters = null,
            CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);
    }

    [Test]
    public async Task SearchAsync_Substring_Matches_Across_Sources()
    {
        var wpts = new FakeWaypointApi();
        wpts.Waypoints.Add(new SignalkWaypoint
            { Id = "1", Name = "Anchorage Cay", Latitude = 24.7, Longitude = -81.1 });
        wpts.Waypoints.Add(new SignalkWaypoint
            { Id = "2", Name = "Marathon Marina", Latitude = 24.7, Longitude = -81.1 });

        var idx = new OwnPlacesIndex(
            wpts, new FakeNoteApi(), new FakeRegionApi(),
            NullLogger<OwnPlacesIndex>.Instance);

        var results = await idx.SearchAsync("anchor");
        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Name).IsEqualTo("Anchorage Cay");

        // Case-insensitive.
        var lower = await idx.SearchAsync("MARATHON");
        await Assert.That(lower.Count).IsEqualTo(1);
        await Assert.That(lower[0].Name).IsEqualTo("Marathon Marina");
    }

    [Test]
    public async Task SearchAsync_Returns_Empty_For_Whitespace_Query()
    {
        var idx = new OwnPlacesIndex(
            new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NullLogger<OwnPlacesIndex>.Instance);

        await Assert.That((await idx.SearchAsync("")).Count).IsEqualTo(0);
        await Assert.That((await idx.SearchAsync("   ")).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Repeat_Search_Within_Ttl_Uses_Cached_Index()
    {
        var time = new FakeTimeProvider(new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc));
        var wpts = new FakeWaypointApi();
        wpts.Waypoints.Add(new SignalkWaypoint
            { Id = "1", Name = "Berlin", Latitude = 52.5, Longitude = 13.4 });

        var idx = new OwnPlacesIndex(
            wpts, new FakeNoteApi(), new FakeRegionApi(),
            NullLogger<OwnPlacesIndex>.Instance,
            time);

        await idx.SearchAsync("ber");
        await idx.SearchAsync("ber");
        await idx.SearchAsync("ber");
        // Three searches; one API load (cache holds within TTL).
        await Assert.That(wpts.LoadCount).IsEqualTo(1);
    }

    [Test]
    public async Task Search_After_Ttl_Reloads()
    {
        var time = new FakeTimeProvider(new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc));
        var wpts = new FakeWaypointApi();
        wpts.Waypoints.Add(new SignalkWaypoint
            { Id = "1", Name = "Berlin", Latitude = 52.5, Longitude = 13.4 });

        var idx = new OwnPlacesIndex(
            wpts, new FakeNoteApi(), new FakeRegionApi(),
            NullLogger<OwnPlacesIndex>.Instance,
            time);

        await idx.SearchAsync("ber");
        await Assert.That(wpts.LoadCount).IsEqualTo(1);

        // Push past the TTL (5 min) and search again.
        time.Advance(OwnPlacesIndex.StaleTtl + TimeSpan.FromSeconds(1));
        await idx.SearchAsync("ber");
        await Assert.That(wpts.LoadCount).IsEqualTo(2);
    }

    [Test]
    public async Task Invalidate_Drops_Cache_So_Next_Search_Refetches()
    {
        var wpts = new FakeWaypointApi();
        wpts.Waypoints.Add(new SignalkWaypoint
            { Id = "1", Name = "Berlin", Latitude = 52.5, Longitude = 13.4 });
        var idx = new OwnPlacesIndex(
            wpts, new FakeNoteApi(), new FakeRegionApi(),
            NullLogger<OwnPlacesIndex>.Instance);

        await idx.SearchAsync("ber");
        await Assert.That(wpts.LoadCount).IsEqualTo(1);

        idx.Invalidate();
        await idx.SearchAsync("ber");
        await Assert.That(wpts.LoadCount).IsEqualTo(2);
    }

    /// <summary>FakeWaypointApi variant whose GetAllAsync parks on a
    /// TaskCompletionSource until the test releases it. Used to drive
    /// concurrent-first-call coalescing + invalidate-mid-load races
    /// deterministically (no Task.Delay sleeps).</summary>
    private sealed class GatedWaypointApi : IWaypointApi
    {
        public List<SignalkWaypoint> Waypoints { get; } = [];
        public int LoadCount { get; private set; }
        public TaskCompletionSource Gate { get; } = new();
        public TaskCompletionSource Started { get; } = new();
        public async Task<List<SignalkWaypoint>> GetAllAsync(CancellationToken ct = default)
        {
            LoadCount++;
            Started.TrySetResult();
            await Gate.Task;
            return Waypoints.ToList();
        }
        public Task<ApiResult<string>> CreateAsync(string name, double lat, double lon, string? description = null, CancellationToken ct = default)
            => Task.FromResult(ApiResult<string>.Ok(""));
        public Task<ApiResult<string>> CreateAsync(string name, double lat, double lon, string? description,
            bool? isMob, bool? isActive, string? mobAlarmId, CancellationToken ct = default)
            => Task.FromResult(ApiResult<string>.Ok(""));
        public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> UpdateAsync(SignalkWaypoint waypoint, string newName, string? newDescription = null, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> UpdateAsync(SignalkWaypoint waypoint, string newName, string? newDescription,
            bool? isMob, bool? isActive, string? mobAlarmId, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> PutWithIdAsync(string id, string name, double lat, double lon,
            string? description, bool? isMob, bool? isActive, string? mobAlarmId,
            CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
    }

    [Test]
    public async Task Concurrent_First_Calls_Coalesce_To_One_Api_Load()
    {
        // Two SearchAsync calls landing within the same render pass
        // must NOT each trigger their own three-API fetch - the
        // _inFlightLoad gate is the production invariant. A regression
        // (e.g. clearing the field too early, swapping the order of
        // assign+await) would silently double the API load on every
        // first paint of the search box.
        var wpts = new GatedWaypointApi();
        wpts.Waypoints.Add(new SignalkWaypoint
            { Id = "1", Name = "Berlin", Latitude = 52.5, Longitude = 13.4 });
        var idx = new OwnPlacesIndex(
            wpts, new FakeNoteApi(), new FakeRegionApi(),
            NullLogger<OwnPlacesIndex>.Instance);

        // Kick off two concurrent searches; both reach EnsureLoadedAsync
        // before either Task.WhenAll inside LoadAsync resolves.
        var first = idx.SearchAsync("ber");
        var second = idx.SearchAsync("ber");

        // Wait for the load to actually start (so we know both
        // SearchAsync calls have run their EnsureLoadedAsync prelude).
        await wpts.Started.Task;
        // LoadCount is incremented at the top of GetAllAsync; the
        // coalescer means it should NEVER hit 2 even with two
        // concurrent SearchAsync waiters.
        await Assert.That(wpts.LoadCount).IsEqualTo(1);

        wpts.Gate.SetResult();
        await Task.WhenAll(first, second);

        await Assert.That(wpts.LoadCount).IsEqualTo(1);
        await Assert.That((await first).Count).IsEqualTo(1);
        await Assert.That((await second).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Invalidate_Mid_Load_Discards_Stale_Result_Forces_Reload()
    {
        // Race: SearchAsync fires LoadAsync against the pre-CRUD view;
        // the helm's CRUD lands and Map.razor calls Invalidate(); the
        // load completes with PRE-CRUD data and would (without the
        // generation guard) install it as the current cache with a
        // fresh timestamp - which is exactly what Invalidate exists
        // to prevent. The generation-counter guard discards the
        // stale load; the next SearchAsync triggers a real reload.
        var wpts = new GatedWaypointApi();
        // Pre-CRUD: empty.
        var idx = new OwnPlacesIndex(
            wpts, new FakeNoteApi(), new FakeRegionApi(),
            NullLogger<OwnPlacesIndex>.Instance);

        // Kick off a search; LoadAsync awaits the gate.
        var firstSearch = idx.SearchAsync("ber");
        await wpts.Started.Task;

        // CRUD lands. The waypoint API now reflects the new entry
        // (simulate by adding to Waypoints) - but the in-flight load
        // captured an empty Waypoints list before the add by holding
        // a reference at the gate.
        // For the test we model this by adding to the API state AND
        // calling Invalidate. The in-flight load will see the modified
        // list when it eventually returns (it reads _waypoints again
        // after the gate releases), but the GENERATION CHECK should
        // discard it. Then the second SearchAsync triggers a fresh
        // load that returns the post-CRUD state.
        idx.Invalidate();

        // Release the gated load. It returns whatever's currently in
        // wpts.Waypoints; the generation guard discards it.
        wpts.Waypoints.Add(new SignalkWaypoint
            { Id = "1", Name = "Berlin Anchorage", Latitude = 52.5, Longitude = 13.4 });
        wpts.Gate.SetResult();
        var firstResult = await firstSearch;
        // The first search returns whatever the (now-discarded) load
        // had in _entries - which is null because Invalidate ran. So
        // SearchAsync re-checks _entries after EnsureLoadedAsync and
        // finds null, returning empty. (This is a corner of the
        // current API: the in-flight-load coalescer doesn't retry
        // when its result is discarded; the next SearchAsync triggers
        // a fresh load.)
        await Assert.That(firstResult.Count).IsEqualTo(0);

        // Second call after invalidate: triggers a fresh load that
        // sees the post-CRUD state. Use a new gate for this load.
        var secondGate = new TaskCompletionSource();
        wpts.Gate.TrySetResult(); // already set; harmless
        // Reset Started for the second load.
        var secondLoadStarted = new TaskCompletionSource();
        // Switch the gate behaviour by replacing the API.
        // Simpler: just call SearchAsync again; the gate is already
        // set so the new GetAllAsync resolves immediately.
        var second = await idx.SearchAsync("ber");
        await Assert.That(wpts.LoadCount).IsEqualTo(2);
        await Assert.That(second.Count).IsEqualTo(1);
        await Assert.That(second[0].Name).IsEqualTo("Berlin Anchorage");
    }
}
