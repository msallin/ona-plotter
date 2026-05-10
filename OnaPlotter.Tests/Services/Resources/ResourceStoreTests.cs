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
    // --- Shared test fakes live in ResourceTestFakes.cs ------------------
    //
    // FakeRouteApi / FakeWaypointApi / FakeNoteApi / FakeRegionApi /
    // ThrowingRouteApi / FakeBaseUrl are shared with ResourceLifecycleTests.

    /// <summary>SignalkClient stub that exposes only the surface
    /// ResourceStore consumes: <see cref="SignalkClient.OnResourceDelta"/>
    /// and <see cref="SignalkClient.OnConnectionChanged"/>. The real
    /// SignalkClient ctor has many dependencies but does NOT spin the
    /// WS pump until StartAsync is called - a fully-wired no-op
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
    public async Task Delta_Lifts_Mob_Metadata_From_Feature_Properties()
    {
        // MOB-as-waypoint composition contract: the WS-delta path must
        // lift isMob/isActive/mobAlarmId from feature.properties onto
        // the flat SignalkWaypoint projection so MobService and the
        // chart-marker layer can read them without walking the GeoJSON
        // tree. A regression in the helper (typo, dropped field) would
        // render a real MOB as a regular dot after a reconnect.
        var store = new ResourceStore(
            new FakeRouteApi(), new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);

        var doc = """{"name":"MOB: 14:32:07","feature":{"type":"Feature","geometry":{"type":"Point","coordinates":[8.5,47.5]},"properties":{"isMob":true,"isActive":true,"mobAlarmId":"alarm-id-abc"}}}""";
        store.HandleResourceDelta("waypoints", "mob1", JsonDocument.Parse(doc).RootElement);

        var wp = store.GetWaypoint("mob1");
        await Assert.That(wp).IsNotNull();
        await Assert.That(wp!.IsMob).IsTrue();
        await Assert.That(wp.IsMobActive).IsTrue();
        await Assert.That(wp.MobAlarmId).IsEqualTo("alarm-id-abc");
    }

    [Test]
    public async Task Delta_NonMob_Waypoint_Has_False_Mob_Flags()
    {
        // Pin the absent-key default contract: a regular waypoint
        // (no MOB block in feature.properties) lifts to false / null,
        // not to a "fail-safe-true" that would render every legacy
        // waypoint as a MOB.
        var store = new ResourceStore(
            new FakeRouteApi(), new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);

        var doc = """{"name":"Buoy","feature":{"type":"Feature","geometry":{"type":"Point","coordinates":[8.5,47.5]},"properties":{"description":""}}}""";
        store.HandleResourceDelta("waypoints", "buoy1", JsonDocument.Parse(doc).RootElement);

        var wp = store.GetWaypoint("buoy1");
        await Assert.That(wp).IsNotNull();
        await Assert.That(wp!.IsMob).IsFalse();
        await Assert.That(wp.IsMobActive).IsFalse();
        await Assert.That(wp.MobAlarmId).IsNull();
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

    [Test]
    public async Task Routes_Snapshot_Is_Reused_Across_Reads_Until_Mutation()
    {
        // PERF-001: the Routes / Waypoints / Notes / Regions snapshots
        // are cached and only rebuilt when the underlying dictionary
        // mutates. A handler that hits Routes twice in a row (e.g. a
        // HUD that reads Count and then iterates) gets the same list
        // reference. After a delta lands, the next read rebuilds.
        var store = new ResourceStore(
            new FakeRouteApi(), new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);

        var doc = """{"name":"Berlin","feature":{"type":"Feature","geometry":{"type":"LineString","coordinates":[[13.4,52.5],[13.5,52.6]]}}}""";
        store.HandleResourceDelta("routes", "r1", JsonDocument.Parse(doc).RootElement);

        var first = store.Routes;
        var second = store.Routes;
        await Assert.That(ReferenceEquals(first, second)).IsTrue();

        // Add another route - next Routes read must be a fresh list.
        store.HandleResourceDelta("routes", "r2", JsonDocument.Parse(doc).RootElement);
        var third = store.Routes;
        await Assert.That(ReferenceEquals(first, third)).IsFalse();
        await Assert.That(third.Count).IsEqualTo(2);

        // The previously-handed-out 'first' reference is still safe to
        // iterate - it's the snapshot at its time of read, with the
        // single 'r1' entry. (Validates the contract that cached
        // snapshots are immutable from the consumer's POV.)
        await Assert.That(first.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Routes_Snapshot_Invalidates_On_Delete_Delta()
    {
        // Delete branch of HandleRouteDelta also nulls the snapshot
        // cache. Without that, a "removed" route would keep showing
        // up in subsequent Routes reads until the next add/update.
        var routeApi = new FakeRouteApi();
        routeApi.Routes.Add(NewRoute("r1", "Berlin"));

        var store = new ResourceStore(
            routeApi, new FakeWaypointApi(), new FakeNoteApi(), new FakeRegionApi(),
            NewStubClient(), NullLogger<ResourceStore>.Instance);
        await store.RefreshAllAsync();

        var before = store.Routes;
        await Assert.That(before.Count).IsEqualTo(1);

        store.HandleResourceDelta("routes", "r1", JsonDocument.Parse("null").RootElement);

        var after = store.Routes;
        await Assert.That(ReferenceEquals(before, after)).IsFalse();
        await Assert.That(after.Count).IsEqualTo(0);
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
