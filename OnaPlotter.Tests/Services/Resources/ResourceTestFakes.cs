using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests.Services.Resources;

/// <summary>
/// Shared <c>I*Api</c> fakes for the resource-store fixtures. Both
/// <see cref="ResourceStoreTests"/> (unit-level) and
/// <see cref="ResourceLifecycleTests"/> (integration-level) drive the
/// same store + the same SK delta path; lifting the fakes up here
/// keeps a single point of update when the production interfaces grow
/// a method (otherwise the two fixtures drift in lockstep).
/// </summary>
internal sealed class FakeRouteApi : IRouteApi
{
    public List<SignalkRoute> Routes { get; } = [];
    public int LoadCount { get; private set; }
    /// <summary>Optional pre-return hook so a test can gate the GetAllAsync
    /// task on a TaskCompletionSource (lets us drive coalesce-during-flight
    /// + dispose-during-flight scenarios deterministically).</summary>
    public Func<Task>? LoadHook { get; set; }
    public async Task<List<SignalkRoute>> GetAllAsync(CancellationToken ct = default)
    {
        LoadCount++;
        if (LoadHook is { } hook) await hook();
        return Routes.ToList();
    }
    public Task<double[][]?> GetCoordinatesAsync(string href, CancellationToken ct = default)
        => Task.FromResult<double[][]?>(null);
    public Task<ApiResult<string>> SaveAsync(string name, double[][] coordsLatLon, CancellationToken ct = default)
        => Task.FromResult(ApiResult<string>.Ok(""));
    public Task<ApiResult> UpdateAsync(string id, string name, double[][] coordsLatLon, CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok);
    public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok);
}

internal sealed class FakeWaypointApi : IWaypointApi
{
    public List<SignalkWaypoint> Waypoints { get; } = [];
    public Task<List<SignalkWaypoint>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult(Waypoints.ToList());
    public Task<ApiResult<string>> CreateAsync(string name, double lat, double lon, string? description = null, CancellationToken ct = default)
        => Task.FromResult(ApiResult<string>.Ok(""));
    public Task<ApiResult<string>> CreateAsync(string name, double lat, double lon, string? description,
        bool? isMob, bool? isActive, string? mobAlarmId, CancellationToken ct = default)
        => Task.FromResult(ApiResult<string>.Ok(""));
    public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok);
    public Task<ApiResult> UpdateAsync(SignalkWaypoint wp, string newName, string? newDescription = null, CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok);
    public Task<ApiResult> UpdateAsync(SignalkWaypoint wp, string newName, string? newDescription,
        bool? isMob, bool? isActive, string? mobAlarmId, CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok);
    public Task<ApiResult> PutWithIdAsync(string id, string name, double lat, double lon,
        string? description, bool? isMob, bool? isActive, string? mobAlarmId,
        CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok);
}

internal sealed class FakeNoteApi : INoteApi
{
    public List<SignalkNote> Notes { get; } = [];
    public Task<List<SignalkNote>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult(Notes.ToList());
    public Task<ApiResult<string>> CreateAsync(string title, string description, double lat, double lon, CancellationToken ct = default)
        => Task.FromResult(ApiResult<string>.Ok(""));
    public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok);
    public Task<ApiResult> UpdateAsync(SignalkNote n, string newTitle, string? newDescription = null, CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok);
}

internal sealed class FakeRegionApi : IRegionApi
{
    public List<SignalkRegion> Regions { get; } = [];
    public Task<List<SignalkRegion>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult(Regions.ToList());
    public Task<ApiResult<string>> CreateCircleAsync(string name, string description, double lat, double lon, double radiusMeters, bool isHazard = false, CancellationToken ct = default)
        => Task.FromResult(ApiResult<string>.Ok(""));
    public Task<ApiResult<string>> CreatePolygonAsync(string name, string description, double[][] vertices, bool isHazard = false, CancellationToken ct = default)
        => Task.FromResult(ApiResult<string>.Ok(""));
    public Task<ApiResult> UpdatePolygonAsync(string id, string name, string description, double[][] vertices,
        bool isHazard = false, DateTime? createdAt = null,
        double? centerLat = null, double? centerLon = null, double? radiusMeters = null,
        CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok);
    public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok);
}

/// <summary>RouteApi fake that throws on every call; used to drive
/// the reconcile-on-reconnect failure path. Other fakes still return
/// success so per-type degradation is visible.</summary>
internal sealed class ThrowingRouteApi : IRouteApi
{
    public Task<List<SignalkRoute>> GetAllAsync(CancellationToken ct = default)
        => throw new HttpRequestException("simulated server outage");
    public Task<double[][]?> GetCoordinatesAsync(string href, CancellationToken ct = default)
        => Task.FromResult<double[][]?>(null);
    public Task<ApiResult<string>> SaveAsync(string name, double[][] coordsLatLon, CancellationToken ct = default)
        => Task.FromResult(ApiResult<string>.Ok(""));
    public Task<ApiResult> UpdateAsync(string id, string name, double[][] coordsLatLon, CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok);
    public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok);
}

internal sealed class FakeBaseUrl : ISignalKBaseUrl
{
    public string BaseUrl => "http://test/";
    public event Action? OnBaseUrlChanged { add { } remove { } }
    public string Combine(string path) => "http://test" + path;
    public Uri StreamUri(string subscribe = "none") => new("ws://test/signalk/v1/stream");
}
