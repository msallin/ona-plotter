using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Places;

namespace OnaPlotter.Tests.Services.Places;

/// <summary>
/// Pins the merge order (own-data first, online after) and the
/// concurrency contract (the inner geocoder is awaited in parallel
/// with the own-data lookup, not after).
/// </summary>
public class MergedPlaceSearchServiceTests
{
    private sealed class StubInner : IPlaceSearchService
    {
        public IReadOnlyList<PlaceResult> NextResults { get; set; } = [];
        public TaskCompletionSource? Gate { get; set; }
        public async Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct = default)
        {
            if (Gate is not null) await Gate.Task;
            return NextResults;
        }
    }

    private sealed class FakeWaypointApi : IWaypointApi
    {
        public List<SignalkWaypoint> Waypoints { get; } = [];
        public Task<List<SignalkWaypoint>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult(Waypoints.ToList());
        public Task<ApiResult<string>> CreateAsync(string name, double lat, double lon, string? description = null, CancellationToken ct = default) =>
            Task.FromResult(ApiResult<string>.Ok(""));
        public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> UpdateAsync(SignalkWaypoint waypoint, string newName, string? newDescription = null, CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);
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
        public Task<ApiResult> UpdatePolygonAsync(string id, string name, string description, double[][] vertices, bool isHazard = false, CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);
    }

    private static OwnPlacesIndex NewOwnIndex(IWaypointApi wpts) =>
        new(wpts, new FakeNoteApi(), new FakeRegionApi(),
            NullLogger<OwnPlacesIndex>.Instance);

    [Test]
    public async Task Empty_Query_Returns_Empty_Without_Calling_Either_Branch()
    {
        var inner = new StubInner { NextResults = new[] { Online("X") } };
        var wpts = new FakeWaypointApi();
        wpts.Waypoints.Add(new SignalkWaypoint
            { Id = "w1", Name = "Wpt", Latitude = 0, Longitude = 0 });
        var svc = new MergedPlaceSearchService(NewOwnIndex(wpts), inner);
        var r = await svc.SearchAsync("   ");
        await Assert.That(r.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Own_Data_Comes_First_Then_Online()
    {
        var inner = new StubInner
        {
            NextResults = new[] { Online("Berlin (Photon)"), Online("Bremen (Photon)") },
        };
        var wpts = new FakeWaypointApi();
        wpts.Waypoints.Add(new SignalkWaypoint
            { Id = "w1", Name = "Berlin Anchorage", Latitude = 52.5, Longitude = 13.4 });

        var svc = new MergedPlaceSearchService(NewOwnIndex(wpts), inner);
        var r = await svc.SearchAsync("ber");

        await Assert.That(r.Count).IsEqualTo(3);
        // Own first.
        await Assert.That(r[0].Source).IsEqualTo("waypoint");
        await Assert.That(r[0].Name).IsEqualTo("Berlin Anchorage");
        // Online after.
        await Assert.That(r[1].Source).IsEqualTo("photon");
        await Assert.That(r[2].Source).IsEqualTo("photon");
    }

    [Test]
    public async Task Empty_Own_Data_Falls_Through_To_Online_Only()
    {
        var inner = new StubInner { NextResults = new[] { Online("Photon hit") } };
        var svc = new MergedPlaceSearchService(NewOwnIndex(new FakeWaypointApi()), inner);

        var r = await svc.SearchAsync("anything");
        await Assert.That(r.Count).IsEqualTo(1);
        await Assert.That(r[0].Source).IsEqualTo("photon");
    }

    [Test]
    public async Task Branches_Run_Concurrently()
    {
        // Hold the inner geocoder behind a TCS. The own-data branch
        // resolves in-memory immediately. If we awaited inner serially
        // the merged Task would never complete until Gate is released.
        // Resolving in parallel: the merged Task is in-flight after
        // Both inner and own start.
        var gate = new TaskCompletionSource();
        var inner = new StubInner
        {
            NextResults = new[] { Online("late") },
            Gate = gate,
        };
        var wpts = new FakeWaypointApi();
        wpts.Waypoints.Add(new SignalkWaypoint
            { Id = "w1", Name = "Berlin", Latitude = 52.5, Longitude = 13.4 });
        var svc = new MergedPlaceSearchService(NewOwnIndex(wpts), inner);

        var task = svc.SearchAsync("ber");
        // Brief wait so the own-data load has had a chance to schedule
        // (it's pure in-memory after the first cache fill, but the API
        // calls in EnsureLoadedAsync cycle through Task.Yield-equivalents).
        await Task.Delay(50);
        // Inner is still gated; merged Task is waiting on it. Release.
        gate.SetResult();
        var results = await task;
        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results[0].Source).IsEqualTo("waypoint");
        await Assert.That(results[1].Source).IsEqualTo("photon");
    }

    private static PlaceResult Online(string name) =>
        new(name, name, 0, 0, "photon");
}
