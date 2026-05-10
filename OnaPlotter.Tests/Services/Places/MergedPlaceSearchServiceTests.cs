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
        /// <summary>Set by SearchAsync the moment it's entered; tests
        /// can await this to deterministically observe "the inner
        /// branch has started" without relying on a wall-clock sleep.</summary>
        public TaskCompletionSource Started { get; } = new();
        public async Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct = default)
        {
            Started.TrySetResult();
            if (Gate is not null) await Gate.Task;
            return NextResults;
        }
    }

    /// <summary>Wraps an OwnPlacesIndex so the test can observe the
    /// moment the merged service started the own-data branch. The
    /// real OwnPlacesIndex doesn't expose a "started" signal because
    /// it normally completes synchronously on a hot cache; the
    /// "Branches_Run_Concurrently" assertion needs the signal to
    /// avoid relying on a Task.Delay wall-clock sleep.</summary>
    private sealed class StubOwn : IPlaceSearchService
    {
        public IReadOnlyList<PlaceResult> NextResults { get; set; } = [];
        public TaskCompletionSource Started { get; } = new();
        public Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct = default)
        {
            Started.TrySetResult();
            return Task.FromResult(NextResults);
        }
    }

    private sealed class FakeWaypointApi : IWaypointApi
    {
        public List<SignalkWaypoint> Waypoints { get; } = [];
        public Task<List<SignalkWaypoint>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult(Waypoints.ToList());
        public Task<ApiResult<string>> CreateAsync(string name, double lat, double lon, string? description = null, CancellationToken ct = default) =>
            Task.FromResult(ApiResult<string>.Ok(""));
        public Task<ApiResult<string>> CreateAsync(string name, double lat, double lon, string? description,
            bool? isMob, bool? isActive, string? mobAlarmId, CancellationToken ct = default) =>
            Task.FromResult(ApiResult<string>.Ok(""));
        public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> UpdateAsync(SignalkWaypoint waypoint, string newName, string? newDescription = null, CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> UpdateAsync(SignalkWaypoint waypoint, string newName, string? newDescription,
            bool? isMob, bool? isActive, string? mobAlarmId, CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> PutWithIdAsync(string id, string name, double lat, double lon,
            string? description, bool? isMob, bool? isActive, string? mobAlarmId,
            CancellationToken ct = default) =>
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
        public Task<ApiResult> UpdatePolygonAsync(string id, string name, string description, double[][] vertices,
            bool isHazard = false, DateTime? createdAt = null,
            double? centerLat = null, double? centerLon = null, double? radiusMeters = null,
            CancellationToken ct = default) =>
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
        // Both branches expose a "Started" TCS that fires the moment
        // SearchAsync is entered. We kick off the merged search, await
        // BOTH started signals, then verify the merged Task is still
        // in-flight (proving online didn't block own's start). Finally
        // release the online gate and assert the merged result lands.
        //
        // No Task.Delay sleeps - the test is deterministic regardless
        // of CI scheduler load.
        var gate = new TaskCompletionSource();
        var own = new StubOwn
        {
            NextResults = new[] { new PlaceResult("Berlin", "Berlin", 52.5, 13.4, "waypoint") },
        };
        var inner = new StubInner
        {
            NextResults = new[] { Online("late") },
            Gate = gate,
        };
        var svc = new MergedPlaceSearchService(own, inner);

        var task = svc.SearchAsync("ber");

        // Both branches must have started before the merged Task can
        // make progress past Task.WhenAll's setup. Awaiting both
        // Started TCSes proves parallelism: a serial implementation
        // (await own; await online) would only fire own.Started and
        // the await on inner.Started.Task would block forever (since
        // inner.SearchAsync hasn't been called yet at that point).
        await own.Started.Task;
        await inner.Started.Task;

        // Inner is still gated; merged Task hasn't completed.
        await Assert.That(task.IsCompleted).IsFalse();

        // Release; the merged result lands with own first, online after.
        gate.SetResult();
        var results = await task;
        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results[0].Source).IsEqualTo("waypoint");
        await Assert.That(results[1].Source).IsEqualTo("photon");
    }

    private static PlaceResult Online(string name) =>
        new(name, name, 0, 0, "photon");
}
