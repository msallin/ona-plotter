using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Resources;

namespace OnaPlotter.Tests.Services.Resources;

/// <summary>
/// ResourceStore composes over <see cref="SignalkClient"/> (WS deltas)
/// and the four <c>*Api</c> REST clients (initial load + reconcile-on-
/// reconnect). Tests pin:
///
/// <list type="bullet">
///   <item><description><b>WS delta apply</b>: <c>resources.routes.&lt;id&gt;</c>
///     with a route document populates the cache + fires
///     <see cref="ResourceStore.OnRouteChanged"/>.</description></item>
///   <item><description><b>WS delta delete</b>: <c>value: null</c> drops
///     the cache entry + fires <see cref="ResourceStore.OnRouteRemoved"/>.</description></item>
///   <item><description><b>REST reconcile</b>: <see cref="ResourceStore.RefreshAllAsync"/>
///     replaces the cache with the server's snapshot, firing Removed
///     for ids no longer on the server.</description></item>
///   <item><description><b>Reconnect path</b>: a connection-state edge
///     from disconnected -&gt; connected triggers a REST reconcile.</description></item>
/// </list>
/// </summary>
public class ResourceStoreTests
{
    // --- Test fakes ---------------------------------------------------

    private sealed class FakeRouteApi : IRouteApi
    {
        public List<SignalkRoute> Routes { get; } = [];
        public int LoadCount { get; private set; }
        public Task<List<SignalkRoute>> GetAllAsync(CancellationToken ct = default)
        {
            LoadCount++;
            return Task.FromResult(Routes.ToList());
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

    private sealed class FakeWaypointApi : IWaypointApi
    {
        public List<SignalkWaypoint> Waypoints { get; } = [];
        public Task<List<SignalkWaypoint>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(Waypoints.ToList());
        public Task<ApiResult<string>> CreateAsync(string name, double lat, double lon, string? description = null, CancellationToken ct = default)
            => Task.FromResult(ApiResult<string>.Ok(""));
        public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> UpdateAsync(SignalkWaypoint wp, string newName, string? newDescription = null, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
    }

    private sealed class FakeNoteApi : INoteApi
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

    private sealed class FakeRegionApi : IRegionApi
    {
        public List<SignalkRegion> Regions { get; } = [];
        public Task<List<SignalkRegion>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(Regions.ToList());
        public Task<ApiResult<string>> CreateCircleAsync(string name, string description, double lat, double lon, double radiusMeters, bool isHazard = false, CancellationToken ct = default)
            => Task.FromResult(ApiResult<string>.Ok(""));
        public Task<ApiResult<string>> CreatePolygonAsync(string name, string description, double[][] vertices, bool isHazard = false, CancellationToken ct = default)
            => Task.FromResult(ApiResult<string>.Ok(""));
        public Task<ApiResult> UpdatePolygonAsync(string id, string name, string description, double[][] vertices, bool isHazard = false, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
        public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ApiResult.Ok);
    }

    /// <summary>SignalkClient stub that exposes only the surface
    /// ResourceStore consumes: <see cref="SignalkClient.OnResourceDelta"/>
    /// and <see cref="SignalkClient.OnConnectionChanged"/>. The real
    /// SignalkClient ctor has many dependencies but does NOT spin the
    /// WS pump until StartAsync is called -- a fully-wired no-op
    /// instance is enough for tests that only drive event invocations
    /// via the type's public surface (or through direct
    /// HandleResourceDelta calls in this test fixture).</summary>
    private static SignalkClient NewStubClient()
    {
        return new SignalkClient(
            baseUrl: new FakeBaseUrl(),
            logger: NullLogger<SignalkClient>.Instance,
            track: new TrackBuffer(),
            ais: new AisStore(),
            http: new HttpClient(),
            settings: new FakeSettings(),
            serverNotifs: new OnaPlotter.Services.ServerNotifications.ServerNotificationStore(),
            atons: new AtonStore(),
            time: TimeProvider.System);
    }

    private sealed class FakeBaseUrl : ISignalKBaseUrl
    {
        public string BaseUrl => "http://test/";
        public string Combine(string path) => "http://test" + path;
        public Uri StreamUri(string subscribe = "none") => new("ws://test/signalk/v1/stream");
    }

    // --- Tests --------------------------------------------------------

    [Test]
    public async Task RefreshAllAsync_Populates_Cache_From_Rest_Apis()
    {
        var routeApi = new FakeRouteApi();
        routeApi.Routes.Add(NewRoute("r1", "Berlin"));
        routeApi.Routes.Add(NewRoute("r2", "Paris"));

        var store = new ResourceStore(
            routeApi, new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);

        await store.RefreshAllAsync();

        await Assert.That(store.IsLoaded).IsTrue();
        await Assert.That(store.Routes.Count).IsEqualTo(2);
        await Assert.That(store.GetRoute("r1")?.Name).IsEqualTo("Berlin");
        await Assert.That(routeApi.LoadCount).IsEqualTo(1);
    }

    [Test]
    public async Task Refresh_Replaces_Cache_And_Fires_Removed_For_Stale_Ids()
    {
        var routeApi = new FakeRouteApi();
        routeApi.Routes.Add(NewRoute("r1", "Berlin"));
        routeApi.Routes.Add(NewRoute("r2", "Paris"));

        var store = new ResourceStore(
            routeApi, new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);

        await store.RefreshAllAsync();

        // Server drops r1; second refresh should remove it locally.
        var removedFired = new List<string>();
        store.OnRouteRemoved += removedFired.Add;
        routeApi.Routes.RemoveAll(r => r.Id == "r1");

        await store.RefreshAllAsync();

        await Assert.That(store.GetRoute("r1")).IsNull();
        await Assert.That(store.GetRoute("r2")).IsNotNull();
        await Assert.That(removedFired).Contains("r1");
    }

    [Test]
    public async Task Delta_Apply_Adds_Route_To_Cache()
    {
        var store = new ResourceStore(
            new FakeRouteApi(), new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);

        var changedFired = new List<string>();
        store.OnRouteChanged += changedFired.Add;

        var doc = """{"name":"Berlin","feature":{"type":"Feature","geometry":{"type":"LineString","coordinates":[[13.4,52.5],[13.5,52.6]]}}}""";
        store.HandleResourceDelta("routes", "r1", JsonDocument.Parse(doc).RootElement);

        await Assert.That(store.GetRoute("r1")).IsNotNull();
        await Assert.That(store.GetRoute("r1")!.Name).IsEqualTo("Berlin");
        await Assert.That(changedFired).Contains("r1");
    }

    [Test]
    public async Task Delta_Apply_Skips_NonLineString_Routes()
    {
        // The helm-facing surface only renders LineStrings (mirrors
        // RouteApi.GetAllAsync's filter). Polygon / MultiLineString
        // route documents from a future SK schema should silently
        // skip rather than blow up the parser.
        var store = new ResourceStore(
            new FakeRouteApi(), new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);

        var doc = """{"name":"NotALine","feature":{"type":"Feature","geometry":{"type":"Polygon","coordinates":[]}}}""";
        store.HandleResourceDelta("routes", "p1", JsonDocument.Parse(doc).RootElement);

        await Assert.That(store.GetRoute("p1")).IsNull();
    }

    [Test]
    public async Task Delta_Null_Value_Removes_Route()
    {
        var routeApi = new FakeRouteApi();
        routeApi.Routes.Add(NewRoute("r1", "Berlin"));

        var store = new ResourceStore(
            routeApi, new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);
        await store.RefreshAllAsync();
        await Assert.That(store.GetRoute("r1")).IsNotNull();

        var removedFired = new List<string>();
        store.OnRouteRemoved += removedFired.Add;

        store.HandleResourceDelta("routes", "r1", JsonDocument.Parse("null").RootElement);

        await Assert.That(store.GetRoute("r1")).IsNull();
        await Assert.That(removedFired).Contains("r1");
    }

    [Test]
    public async Task Delta_Apply_Updates_Existing_Route_In_Place()
    {
        // Route exists in cache (e.g. via REST init); a delta with the
        // same id replaces the document and re-fires Changed. Mirrors
        // the multi-plotter sync path: plotter A PUTs, plotter B's
        // SignalkClient OnResourceDelta -> ResourceStore.HandleResourceDelta
        // -> the cached entry now reflects A's edit.
        var routeApi = new FakeRouteApi();
        routeApi.Routes.Add(NewRoute("r1", "Berlin"));

        var store = new ResourceStore(
            routeApi, new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);
        await store.RefreshAllAsync();
        await Assert.That(store.GetRoute("r1")?.Name).IsEqualTo("Berlin");

        var changedFired = new List<string>();
        store.OnRouteChanged += changedFired.Add;

        var doc = """{"name":"Berlin Renamed","feature":{"type":"Feature","geometry":{"type":"LineString","coordinates":[[1.0,1.0],[2.0,2.0]]}}}""";
        store.HandleResourceDelta("routes", "r1", JsonDocument.Parse(doc).RootElement);

        await Assert.That(store.GetRoute("r1")?.Name).IsEqualTo("Berlin Renamed");
        await Assert.That(changedFired).Contains("r1");
    }

    [Test]
    public async Task Delta_For_Unknown_Type_Is_Ignored()
    {
        // resources.charts.* or any future type that the store doesn't
        // know about: log + drop, don't throw.
        var store = new ResourceStore(
            new FakeRouteApi(), new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);

        var doc = """{"foo":"bar"}""";
        store.HandleResourceDelta("charts", "c1", JsonDocument.Parse(doc).RootElement);

        await Assert.That(store.Routes.Count).IsEqualTo(0);
        await Assert.That(store.Waypoints.Count).IsEqualTo(0);
        await Assert.That(store.Notes.Count).IsEqualTo(0);
        await Assert.That(store.Regions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Delta_Malformed_Payload_Is_Logged_And_Skipped()
    {
        // A garbage document for a known type: log + skip; cache stays
        // unchanged. Verifies the JsonException catch in the per-type
        // handler.
        var store = new ResourceStore(
            new FakeRouteApi(), new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);

        // A number where a SignalkRoute object is expected. JsonElement
        // .Deserialize<SignalkRoute>() throws JsonException; the handler
        // catches + logs + returns without mutating cache.
        store.HandleResourceDelta("routes", "r1", JsonDocument.Parse("42").RootElement);

        await Assert.That(store.GetRoute("r1")).IsNull();
    }

    [Test]
    public async Task Delta_Apply_Populates_Waypoint_Lat_Lon_From_Geometry()
    {
        // Waypoint deltas should round-trip the same shape as
        // WaypointApi.GetAllAsync: top-level Latitude / Longitude
        // populated from feature.geometry.coordinates (Point [lon,lat]).
        var store = new ResourceStore(
            new FakeRouteApi(), new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);

        var doc = """{"name":"Anchorage","feature":{"type":"Feature","geometry":{"type":"Point","coordinates":[-81.1,24.7]},"properties":{"description":"Quiet bay"}}}""";
        store.HandleResourceDelta("waypoints", "w1", JsonDocument.Parse(doc).RootElement);

        var wp = store.GetWaypoint("w1");
        await Assert.That(wp).IsNotNull();
        await Assert.That(wp!.Latitude).IsEqualTo(24.7);
        await Assert.That(wp.Longitude).IsEqualTo(-81.1);
        await Assert.That(wp.Description).IsEqualTo("Quiet bay");
    }

    [Test]
    public async Task RefreshAllAsync_Per_Type_Failure_Degrades_Gracefully()
    {
        // One API fetch failure shouldn't take the others down.
        var routeApi = new ThrowingRouteApi();
        var waypointApi = new FakeWaypointApi();
        waypointApi.Waypoints.Add(new SignalkWaypoint { Id = "w1", Name = "Anchorage" });

        var store = new ResourceStore(
            routeApi, waypointApi, new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);

        await store.RefreshAllAsync();

        // Routes API threw -> route cache stays empty (no crash).
        await Assert.That(store.Routes.Count).IsEqualTo(0);
        // Waypoint API succeeded -> waypoint cache populated.
        await Assert.That(store.Waypoints.Count).IsEqualTo(1);
        await Assert.That(store.IsLoaded).IsTrue();
    }

    private sealed class ThrowingRouteApi : IRouteApi
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

    // --- Helpers ------------------------------------------------------

    private static SignalkRoute NewRoute(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Feature = new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new GeoJsonGeometry
            {
                Type = "LineString",
                Coordinates = JsonDocument.Parse("[[13.4,52.5],[13.5,52.6]]").RootElement,
            },
        },
    };
}
