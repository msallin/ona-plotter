using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Resources;

namespace OnaPlotter.Tests.Services.Resources;

/// <summary>
/// Whole-flow lifecycle tests for the multi-plotter resource sync.
/// The route catalog is owned by <see cref="ResourceStore"/> which
/// composes over <see cref="SignalkClient"/> (WS deltas) and the
/// REST <c>*Api</c> clients (initial load + reconcile-on-reconnect).
/// These tests pin sequences the helm reported breaking before
/// PR #236:
///
/// <list type="bullet">
///   <item><description>Initial load -> activate -> remote edit
///     while the active route is in flight -> cache + active-route
///     identity stay consistent.</description></item>
///   <item><description>Disconnect -> server-side route edit (delta
///     missed) -> reconnect -> REST reconcile picks up the missed
///     change.</description></item>
///   <item><description>Local edit (move waypoint, add, delete) ->
///     server echoes back as a delta -> ResourceStore cache reflects
///     the new geometry.</description></item>
///   <item><description>Route deletion via delta drops the cached
///     entry + fires <see cref="ResourceStore.OnRouteRemoved"/>.</description></item>
///   <item><description>End-to-end through SignalkClient.ProcessMessage
///     with raw delta JSON -- proves the
///     <c>resources.&lt;type&gt;.&lt;id&gt;</c> path-prefix dispatcher
///     wires correctly under realistic SK wire format.</description></item>
/// </list>
/// </summary>
public class ResourceLifecycleTests
{
    // Fakes live in ResourceTestFakes.cs (shared with ResourceStoreTests).

    private static (SignalkClient client, ResourceStore store, FakeRouteApi routes,
                    FakeWaypointApi waypoints, FakeNoteApi notes, FakeRegionApi regions)
        BuildHarness()
    {
        var client = new SignalkClient(
            baseUrl: new FakeBaseUrl(),
            logger: NullLogger<SignalkClient>.Instance,
            track: new TrackBuffer(),
            ais: new AisStore(),
            http: new HttpClient(),
            settings: new FakeSettings(),
            serverNotifs: new OnaPlotter.Services.ServerNotifications.ServerNotificationStore(),
            atons: new AtonStore(),
            time: TimeProvider.System);

        var routes = new FakeRouteApi();
        var waypoints = new FakeWaypointApi();
        var notes = new FakeNoteApi();
        var regions = new FakeRegionApi();
        var store = new ResourceStore(routes, waypoints, notes, regions, client,
            NullLogger<ResourceStore>.Instance);
        return (client, store, routes, waypoints, notes, regions);
    }

    // --- Helpers for building wire-shape route docs --------------------

    /// <summary>Build a SignalkRoute DTO with N waypoints. Used as the
    /// REST-side state and the JSON delta payload below.</summary>
    private static SignalkRoute MakeRoute(string id, string name, params (double Lon, double Lat)[] waypoints)
    {
        var coordsJson = "[" + string.Join(",", waypoints.Select(w =>
            $"[{w.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)},{w.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}]")) + "]";
        return new SignalkRoute
        {
            Id = id,
            Name = name,
            Feature = new GeoJsonFeature
            {
                Type = "Feature",
                Geometry = new GeoJsonGeometry
                {
                    Type = "LineString",
                    Coordinates = JsonDocument.Parse(coordsJson).RootElement,
                },
            },
        };
    }

    /// <summary>Render a SK delta-style JSON envelope with a
    /// <c>resources.routes.&lt;id&gt;</c> path. Mirrors what
    /// signalk-server emits when a client PUTs a route. Drives
    /// <see cref="SignalkClient.ProcessMessage"/> end-to-end.</summary>
    private static string RouteDeltaJson(string id, SignalkRoute route)
    {
        var feature = route.Feature!;
        var coords = feature.Geometry!.Coordinates.GetRawText();
        // The server emits the resource document as the value of the
        // delta (full body on PUT/POST). We mirror that shape.
        return $@"{{
            ""context"":""vessels.self"",
            ""updates"":[{{
                ""timestamp"":""2026-05-07T20:00:00.000Z"",
                ""values"":[{{
                    ""path"":""resources.routes.{id}"",
                    ""value"":{{
                        ""name"":""{route.Name}"",
                        ""feature"":{{
                            ""type"":""Feature"",
                            ""geometry"":{{
                                ""type"":""LineString"",
                                ""coordinates"":{coords}
                            }}
                        }}
                    }}
                }}]
            }}]
        }}";
    }

    private static string DeleteRouteDeltaJson(string id) => $@"{{
        ""context"":""vessels.self"",
        ""updates"":[{{
            ""timestamp"":""2026-05-07T20:00:00.000Z"",
            ""values"":[{{
                ""path"":""resources.routes.{id}"",
                ""value"":null
            }}]
        }}]
    }}";

    // --- 1. Initial load --------------------------------------------------

    [Test]
    public async Task InitialLoad_RestPopulatesStore_IsLoadedTrue()
    {
        var (_, store, routes, _, _, _) = BuildHarness();
        routes.Routes.Add(MakeRoute("r1", "Berlin", (13.4, 52.5), (13.5, 52.6), (13.6, 52.7)));

        await store.RefreshAllAsync();

        await Assert.That(store.IsLoaded).IsTrue();
        await Assert.That(store.Routes.Count).IsEqualTo(1);
        var loaded = store.GetRoute("r1");
        await Assert.That(loaded?.Name).IsEqualTo("Berlin");
        await Assert.That(routes.LoadCount).IsEqualTo(1);
    }

    // --- 2. Activation flow + remote edit while active ------------------

    [Test]
    public async Task ActiveRouteIdentity_RemoteEditViaDelta_CacheReflectsNewGeometryWithoutChangingHref()
    {
        // Helm scenario: route loaded, helm activates it (SK server sets
        // navigation.course.activeRoute.href), THEN another plotter
        // edits the route (e.g. moves a waypoint). This test verifies
        // the CACHE half of the contract -- the route catalog reflects
        // the new geometry while the active-route href stays the same.
        // The active-route OVERLAY redraw is in Map.razor and is not
        // exercised here (would require bUnit + Leaflet stubs); this
        // test is the cache-side guarantee that overlay redraws have
        // accurate data to pull from.
        var (client, store, routes, _, _, _) = BuildHarness();

        // Step 1: initial route v1 (3 WPs).
        routes.Routes.Add(MakeRoute("r1", "Berlin",
            (13.4, 52.5), (13.5, 52.6), (13.6, 52.7)));
        await store.RefreshAllAsync();

        // Step 2: helm activates -- the client tracks active-route
        // identity in NavigationData. Wire the SK delta that the
        // server publishes when the helm activates a route.
        client.ProcessMessage(@"{
            ""context"":""vessels.self"",
            ""updates"":[{
                ""values"":[
                    {""path"":""navigation.course.activeRoute.href"",""value"":""/resources/routes/r1""},
                    {""path"":""navigation.course.nextPoint.position"",""value"":{""latitude"":52.6,""longitude"":13.5}}
                ]
            }]
        }");
        await Assert.That(client.Data.ActiveRouteHref).IsNotNull();

        // Step 3: another plotter edits route -- moves WP[1] from
        // (13.5, 52.6) to (13.55, 52.65) AND adds a new WP at the
        // end (13.7, 52.8). Server emits a delta with the full new
        // document.
        var routeV2 = MakeRoute("r1", "Berlin",
            (13.4, 52.5), (13.55, 52.65), (13.6, 52.7), (13.7, 52.8));
        client.ProcessMessage(RouteDeltaJson("r1", routeV2));

        // Step 4: cache reflects the new geometry (4 WPs). The active-
        // route href identity stays the same -- the overlay layer in
        // Map.razor reads that and decides whether to refetch.
        await Assert.That(WpCount(store.GetRoute("r1"))).IsEqualTo(4);
        await Assert.That(client.Data.ActiveRouteHref).IsEqualTo("/resources/routes/r1");
    }

    // --- 3. Disconnect / reconnect / missed delta ------------------------

    [Test]
    public async Task Disconnect_ServerEditMissed_ReconnectReconcilesViaRest()
    {
        // Helm scenario: route loaded; helm's WS drops (link flap, dock
        // wifi handoff); during the gap another plotter edits the
        // route; helm's WS reconnects. Live deltas during the disconnect
        // window are missed by definition; the reconcile-on-reconnect
        // is the catch-up path. ResourceStore.OnConnectionChanged
        // detects the disconnected -> connected edge and kicks
        // RefreshAllAsync.
        var (client, store, routes, _, _, _) = BuildHarness();

        // Step 1: initial load -- route v1.
        routes.Routes.Add(MakeRoute("r1", "Berlin",
            (13.4, 52.5), (13.5, 52.6)));
        await store.RefreshAllAsync();
        await Assert.That(WpCount(store.GetRoute("r1")))
            .IsEqualTo(2);

        // Step 2: client connects (the initial state from the test
        // harness has _wasConnected=false, so this first connect IS
        // an edge transition that triggers HandleConnectionChange ->
        // ReconcileOnReconnectAsync. LastReconcileTask must be set.
        // Capture the API's LoadCount after this reconcile so step 5
        // can verify the SECOND reconnect actually re-fetched (proves
        // _wasConnected edge detection is working).
        client.MarkConnectionOpened();
        client.RaiseConnectionChanged();
        await Assert.That(store.LastReconcileTask).IsNotNull();
        await store.LastReconcileTask!;
        var loadCountAfterFirstReconcile = routes.LoadCount;

        // Step 3: WS drops.
        client.MarkConnectionClosed();
        client.RaiseConnectionChanged();

        // Step 4: server-side edit during the disconnect window.
        // Update the FakeRouteApi to v2 (3 waypoints); a delta would
        // normally fire but we're "offline" so it doesn't reach us.
        routes.Routes.Clear();
        routes.Routes.Add(MakeRoute("r1", "Berlin",
            (13.4, 52.5), (13.5, 52.6), (13.6, 52.7)));
        // Cache still has v1 -- nothing has refreshed yet.
        await Assert.That(WpCount(store.GetRoute("r1"))).IsEqualTo(2);

        // Step 5: WS reconnects -- store kicks a fire-and-forget
        // RefreshAllAsync. Test awaits the stashed task to completion.
        // The API's LoadCount must increment beyond the post-step-2
        // baseline; a regression where the edge detector short-circuits
        // (e.g. _wasConnected tracking breaks) would leave LoadCount
        // unchanged and the cache stuck on v1.
        client.MarkConnectionOpened();
        client.RaiseConnectionChanged();
        await Assert.That(store.LastReconcileTask).IsNotNull();
        await store.LastReconcileTask!;
        await Assert.That(routes.LoadCount).IsGreaterThan(loadCountAfterFirstReconcile);

        // Step 6: cache now has v2 (3 waypoints) -- reconcile picked
        // up the missed change.
        await Assert.That(WpCount(store.GetRoute("r1"))).IsEqualTo(3);
    }

    // --- 4. Local edits roundtrip back via delta -------------------------

    [Test]
    public async Task LocalEdit_MoveWaypoint_ServerEchoesDelta_CacheReflectsMove()
    {
        // Helm scenario: helm uses the route-edit panel to drag WP[1]
        // to a new location and saves. RouteApi.UpdateAsync PUTs the
        // new geometry; the SK server PUT handler emits a delta back
        // to all subscribers (including this plotter). The store
        // applies the delta, so by the time the local UI re-renders
        // the cache already shows the new position.
        var (client, store, routes, _, _, _) = BuildHarness();
        routes.Routes.Add(MakeRoute("r1", "Berlin",
            (13.4, 52.5), (13.5, 52.6), (13.6, 52.7)));
        await store.RefreshAllAsync();

        // Helm drags WP[1] to a new lon (13.55) / lat (52.65).
        var moved = MakeRoute("r1", "Berlin",
            (13.4, 52.5), (13.55, 52.65), (13.6, 52.7));
        client.ProcessMessage(RouteDeltaJson("r1", moved));

        var afterMove = store.GetRoute("r1");
        await Assert.That(afterMove).IsNotNull();
        // WP[1]'s new lon = 13.55. Verify by reading the second
        // coordinate from the LineString.
        var coords = afterMove!.Feature!.Geometry!.Coordinates;
        var wp1Lon = coords[1][0].GetDouble();
        var wp1Lat = coords[1][1].GetDouble();
        await Assert.That(wp1Lon).IsEqualTo(13.55);
        await Assert.That(wp1Lat).IsEqualTo(52.65);
    }

    [Test]
    public async Task LocalEdit_AddWaypoint_DeltaUpdatesCacheCount()
    {
        var (client, store, routes, _, _, _) = BuildHarness();
        routes.Routes.Add(MakeRoute("r1", "Berlin",
            (13.4, 52.5), (13.5, 52.6)));
        await store.RefreshAllAsync();
        await Assert.That(WpCount(store.GetRoute("r1")))
            .IsEqualTo(2);

        // Helm adds a third waypoint.
        var withNew = MakeRoute("r1", "Berlin",
            (13.4, 52.5), (13.5, 52.6), (13.7, 52.8));
        client.ProcessMessage(RouteDeltaJson("r1", withNew));

        await Assert.That(WpCount(store.GetRoute("r1")))
            .IsEqualTo(3);
    }

    [Test]
    public async Task LocalEdit_DeleteWaypoint_DeltaUpdatesCacheCount()
    {
        var (client, store, routes, _, _, _) = BuildHarness();
        routes.Routes.Add(MakeRoute("r1", "Berlin",
            (13.4, 52.5), (13.5, 52.6), (13.6, 52.7)));
        await store.RefreshAllAsync();
        await Assert.That(WpCount(store.GetRoute("r1")))
            .IsEqualTo(3);

        // Helm deletes WP[1] (the middle one).
        var withRemoved = MakeRoute("r1", "Berlin",
            (13.4, 52.5), (13.6, 52.7));
        client.ProcessMessage(RouteDeltaJson("r1", withRemoved));

        await Assert.That(WpCount(store.GetRoute("r1")))
            .IsEqualTo(2);
    }

    // --- 5. Route deletion via delta ------------------------------------

    [Test]
    public async Task RouteDeletion_DeltaWithNullValue_RemovesAndFiresEvent()
    {
        var (client, store, routes, _, _, _) = BuildHarness();
        routes.Routes.Add(MakeRoute("r1", "Berlin",
            (13.4, 52.5), (13.5, 52.6)));
        await store.RefreshAllAsync();
        await Assert.That(store.GetRoute("r1")).IsNotNull();

        var removed = new List<string>();
        store.OnRouteRemoved += removed.Add;

        client.ProcessMessage(DeleteRouteDeltaJson("r1"));

        await Assert.That(store.GetRoute("r1")).IsNull();
        await Assert.That(removed).Contains("r1");
    }

    // --- 6. Compound: full flow over the SignalkClient.ProcessMessage path

    [Test]
    public async Task FullFlow_LoadActivateEditDelete_StoreStaysConsistent()
    {
        // Compound scenario integrating every step the helm flagged:
        // initial load + activate + remote edit (move + add WP) +
        // delete + verify. End-to-end through the public
        // SignalkClient.ProcessMessage surface so the full delta
        // dispatcher chain (path-prefix scan -> OnResourceDelta ->
        // ResourceStore.HandleResourceDelta -> typed parse) is
        // exercised.
        var (client, store, routes, waypoints, _, _) = BuildHarness();

        // Initial: 2 routes + 1 waypoint.
        routes.Routes.Add(MakeRoute("r1", "Berlin", (13.4, 52.5), (13.5, 52.6)));
        routes.Routes.Add(MakeRoute("r2", "Bremen", (8.8, 53.1), (9.0, 53.2)));
        waypoints.Waypoints.Add(new SignalkWaypoint
        {
            Id = "w1",
            Name = "Anchorage",
            Latitude = 24.7,
            Longitude = -81.1,
        });
        await store.RefreshAllAsync();
        await Assert.That(store.Routes.Count).IsEqualTo(2);
        await Assert.That(store.Waypoints.Count).IsEqualTo(1);

        // Activate r1.
        client.ProcessMessage(@"{
            ""context"":""vessels.self"",
            ""updates"":[{
                ""values"":[
                    {""path"":""navigation.course.activeRoute.href"",""value"":""/resources/routes/r1""},
                    {""path"":""navigation.course.nextPoint.position"",""value"":{""latitude"":52.6,""longitude"":13.5}}
                ]
            }]
        }");

        // Remote edit r1: add WP, move WP[1].
        var r1V2 = MakeRoute("r1", "Berlin",
            (13.4, 52.5), (13.55, 52.65), (13.7, 52.8));
        client.ProcessMessage(RouteDeltaJson("r1", r1V2));

        var routeAfterEdit = store.GetRoute("r1");
        await Assert.That(WpCount(routeAfterEdit)).IsEqualTo(3);
        // Active-route identity should NOT have changed.
        await Assert.That(client.Data.ActiveRouteHref).IsEqualTo("/resources/routes/r1");

        // Delete r2 -- assert BOTH the cache mutation AND the
        // OnRouteRemoved event fire. A regression where the cache
        // drops the entry but the event doesn't fire would leave
        // consumer overlays (Map.razor's leaflet route layer) with
        // a stale polyline; the event is the load-bearing signal.
        var removed = new List<string>();
        store.OnRouteRemoved += removed.Add;
        client.ProcessMessage(DeleteRouteDeltaJson("r2"));
        await Assert.That(store.GetRoute("r2")).IsNull();
        await Assert.That(store.Routes.Count).IsEqualTo(1);
        await Assert.That(removed).Contains("r2");

        // r1 + waypoints intact.
        await Assert.That(store.GetRoute("r1")).IsNotNull();
        await Assert.That(store.GetWaypoint("w1")?.Latitude).IsEqualTo(24.7);
    }

    // --- 7. End-to-end via raw bytes ------------------------------------

    [Test]
    public async Task ProcessMessageBytes_RouteDelta_LandsInStore()
    {
        // The production WS receive loop calls ProcessMessageBytes
        // with a UTF-8 ReadOnlySpan -- not the string overload tests
        // typically use. Verify the bytes path also wires the
        // resource-delta dispatcher, since that's the path SignalkClient
        // actually uses on a live socket.
        var (client, store, _, _, _, _) = BuildHarness();
        var json = RouteDeltaJson("r1", MakeRoute("r1", "Berlin",
            (13.4, 52.5), (13.5, 52.6)));
        var utf8 = Encoding.UTF8.GetBytes(json);

        client.ProcessMessageBytes(utf8);

        await Assert.That(store.GetRoute("r1")?.Name).IsEqualTo("Berlin");
    }

    // --- 8. Multi-resource delta in one message -------------------------

    [Test]
    public async Task SingleDelta_MultipleResourceUpdates_AllReflected()
    {
        // The SK delta protocol allows multiple values in one update.
        // A composite delta (route + waypoint + note + region in one
        // message) should flow each value through the right typed
        // handler. Each payload uses a realistic shape for its type
        // (LineString for the route, Point for the waypoint, position
        // sub-object for the note, Polygon feature for the region).
        // The route payload is built via the same RouteDeltaJson
        // helper used elsewhere so the wire format stays in one place;
        // waypoint / note / region are embedded inline because they're
        // simpler and only used here.
        var (client, store, _, _, _, _) = BuildHarness();

        var routeBody = MakeRoute("r1", "R", (1.0, 1.0), (2.0, 2.0));
        var routeBodyJson = $@"{{
            ""name"":""{routeBody.Name}"",
            ""feature"":{{
                ""type"":""Feature"",
                ""geometry"":{{
                    ""type"":""LineString"",
                    ""coordinates"":{routeBody.Feature!.Geometry!.Coordinates.GetRawText()}
                }}
            }}
        }}";

        var json = $@"{{
            ""context"":""vessels.self"",
            ""updates"":[{{
                ""values"":[
                    {{""path"":""resources.routes.r1"",""value"":{routeBodyJson}}},
                    {{""path"":""resources.waypoints.w1"",""value"":{{""name"":""W"",""feature"":{{""type"":""Feature"",""geometry"":{{""type"":""Point"",""coordinates"":[3.0,4.0]}}}}}}}},
                    {{""path"":""resources.notes.n1"",""value"":{{""title"":""N"",""position"":{{""latitude"":5.0,""longitude"":6.0}}}}}},
                    {{""path"":""resources.regions.g1"",""value"":{{""name"":""G"",""feature"":{{""type"":""Feature"",""geometry"":{{""type"":""Polygon"",""coordinates"":[[[7.0,8.0],[9.0,8.0],[9.0,10.0],[7.0,10.0],[7.0,8.0]]]}}}}}}}}
                ]
            }}]
        }}";

        client.ProcessMessage(json);

        await Assert.That(store.GetRoute("r1")?.Name).IsEqualTo("R");
        await Assert.That(store.GetWaypoint("w1")?.Name).IsEqualTo("W");
        await Assert.That(store.GetWaypoint("w1")?.Latitude).IsEqualTo(4.0);
        await Assert.That(store.GetWaypoint("w1")?.Longitude).IsEqualTo(3.0);
        await Assert.That(store.GetNote("n1")?.Title).IsEqualTo("N");
        await Assert.That(store.GetRegion("g1")?.Name).IsEqualTo("G");
        // Region geometry round-trips: feature is non-null and
        // carries the Polygon type (the typed parse didn't drop it).
        await Assert.That(store.GetRegion("g1")?.Feature?.Geometry?.Type).IsEqualTo("Polygon");
    }

    // --- 9. Reconnect-reconcile no-edge + failure-path coverage ----------

    [Test]
    public async Task Connected_To_Connected_NoEdge_Does_Not_Trigger_Reconcile()
    {
        // Edge tracker contract: only the false -> true transition kicks
        // ReconcileOnReconnectAsync. A spurious OnConnectionChanged raise
        // while already connected (e.g. a future "still alive" heartbeat
        // event) must NOT cost a four-call REST refresh. Regression
        // protection for the _wasConnected flag.
        var (client, store, routes, _, _, _) = BuildHarness();
        routes.Routes.Add(MakeRoute("r1", "Berlin", (13.4, 52.5), (13.5, 52.6)));

        // Initial transition false -> true: kicks a reconcile.
        client.MarkConnectionOpened();
        client.RaiseConnectionChanged();
        await Assert.That(store.LastReconcileTask).IsNotNull();
        await store.LastReconcileTask!;
        var loadCountAfterFirst = routes.LoadCount;

        // Spurious raise without a state change. _wasConnected is
        // already true; HandleConnectionChange's edge guard returns
        // before assigning a new LastReconcileTask.
        client.RaiseConnectionChanged();
        // Brief yield in case any continuation is queued.
        await Task.Yield();

        // No new reconcile was kicked: API LoadCount didn't budge.
        // (Reference-equality on LastReconcileTask isn't a reliable
        // signal -- async methods that complete synchronously can
        // share Task instances. LoadCount is the direct observable.)
        await Assert.That(routes.LoadCount).IsEqualTo(loadCountAfterFirst);
    }

    [Test]
    public async Task ReconcileOnReconnect_RestFailure_IsSwallowed_StoreStaysUsable()
    {
        // PR #236 added a try/catch around RefreshAllAsync inside
        // ReconcileOnReconnectAsync. If the inner exception escapes,
        // an unobserved task can faulted-state the store; if it's
        // caught but the store enters a broken state, subsequent
        // deltas land on dead caches. This test pins both: the
        // reconcile completes (Task is RanToCompletion, not Faulted)
        // even on a REST failure, and a delta after the failure
        // populates the cache normally.
        var routeApi = new ThrowingRouteApi();
        var waypointApi = new FakeWaypointApi();
        var noteApi = new FakeNoteApi();
        var regionApi = new FakeRegionApi();
        var client = new SignalkClient(
            baseUrl: new FakeBaseUrl(),
            logger: NullLogger<SignalkClient>.Instance,
            track: new TrackBuffer(),
            ais: new AisStore(),
            http: new HttpClient(),
            settings: new FakeSettings(),
            serverNotifs: new OnaPlotter.Services.ServerNotifications.ServerNotificationStore(),
            atons: new AtonStore(),
            time: TimeProvider.System);
        var store = new ResourceStore(routeApi, waypointApi, noteApi, regionApi, client,
            NullLogger<ResourceStore>.Instance);

        // Trigger reconnect-edge -> reconcile fires -> RouteApi throws.
        client.MarkConnectionOpened();
        client.RaiseConnectionChanged();
        await Assert.That(store.LastReconcileTask).IsNotNull();
        await store.LastReconcileTask!;

        // Task completed cleanly (the catch in ReconcileOnReconnectAsync
        // logged + swallowed the inner failure); not Faulted.
        await Assert.That(store.LastReconcileTask!.Status).IsEqualTo(TaskStatus.RanToCompletion);
        // Routes failed -> cache stays empty for routes.
        await Assert.That(store.Routes.Count).IsEqualTo(0);

        // Subsequent delta still works: the store didn't enter a
        // broken state from the catch.
        var route = MakeRoute("r1", "Berlin", (13.4, 52.5), (13.5, 52.6));
        client.ProcessMessage(RouteDeltaJson("r1", route));
        await Assert.That(store.GetRoute("r1")?.Name).IsEqualTo("Berlin");
    }

    // --- 10. Waypoint / Note / Region delta -> store cache --------------

    [Test]
    public async Task RemoteWaypointEdit_DeltaUpdatesCachedPosition()
    {
        // Helm scenario: another plotter drags a waypoint to a new lat/
        // lon. Server emits resources.waypoints.<id> with the new Point.
        // ResourceStore parses the GeoJSON Feature shape and hoists the
        // top-level Latitude / Longitude on the DTO, mirroring
        // WaypointApi.GetAllAsync. Map.razor's HandleWaypointChangedFromStore
        // then redraws the marker (not exercised here -- requires the
        // leaflet JS module).
        var (client, store, _, waypoints, _, _) = BuildHarness();
        waypoints.Waypoints.Add(new SignalkWaypoint
        {
            Id = "w1",
            Name = "Anchorage",
            Latitude = 24.7,
            Longitude = -81.1,
        });
        await store.RefreshAllAsync();
        await Assert.That(store.GetWaypoint("w1")?.Latitude).IsEqualTo(24.7);

        // Remote edit: same id, new position. Wire shape mirrors the
        // SK server's resources.waypoints.<id> delta (full GeoJSON
        // Feature with Point coordinates [lon, lat]).
        var json = @"{
            ""context"":""vessels.self"",
            ""updates"":[{
                ""values"":[
                    {""path"":""resources.waypoints.w1"",""value"":{
                        ""name"":""Anchorage"",
                        ""feature"":{
                            ""type"":""Feature"",
                            ""geometry"":{""type"":""Point"",""coordinates"":[-81.05,24.75]},
                            ""properties"":{""description"":""Quiet bay""}
                        }
                    }}
                ]
            }]
        }";
        client.ProcessMessage(json);

        var moved = store.GetWaypoint("w1");
        await Assert.That(moved).IsNotNull();
        await Assert.That(moved!.Latitude).IsEqualTo(24.75);
        await Assert.That(moved.Longitude).IsEqualTo(-81.05);
        await Assert.That(moved.Description).IsEqualTo("Quiet bay");
    }

    [Test]
    public async Task RemoteWaypointDelete_DeltaRemovesAndFiresEvent()
    {
        var (client, store, _, waypoints, _, _) = BuildHarness();
        waypoints.Waypoints.Add(new SignalkWaypoint
        {
            Id = "w1", Name = "Anchorage", Latitude = 24.7, Longitude = -81.1,
        });
        await store.RefreshAllAsync();

        var removed = new List<string>();
        store.OnWaypointRemoved += removed.Add;
        client.ProcessMessage(@"{
            ""context"":""vessels.self"",
            ""updates"":[{""values"":[{""path"":""resources.waypoints.w1"",""value"":null}]}]
        }");

        await Assert.That(store.GetWaypoint("w1")).IsNull();
        await Assert.That(removed).Contains("w1");
    }

    [Test]
    public async Task RemoteRegionEdit_DeltaPopulatesOuterRingsForLeafletRender()
    {
        // The Map.razor region layer renders OuterRings (Leaflet-ordered
        // [lat, lon]) -- not the wire-shape feature.geometry.coordinates
        // ([lon, lat]). RegionApi.GetAllAsync hoists the rings on REST
        // results; ResourceStore's HandleRegionDelta must do the same on
        // delta-fed entries so a remote-edit lands a region the chart
        // can actually draw.
        var (client, store, _, _, _, _) = BuildHarness();

        // Polygon delta (4 vertices + closing). Wire format is GeoJSON
        // [lon, lat]; OuterRings should be [lat, lon] after the hoist.
        var json = @"{
            ""context"":""vessels.self"",
            ""updates"":[{
                ""values"":[
                    {""path"":""resources.regions.g1"",""value"":{
                        ""name"":""TestArea"",
                        ""feature"":{
                            ""type"":""Feature"",
                            ""geometry"":{
                                ""type"":""Polygon"",
                                ""coordinates"":[[
                                    [10.0, 50.0], [11.0, 50.0],
                                    [11.0, 51.0], [10.0, 51.0],
                                    [10.0, 50.0]
                                ]]
                            }
                        }
                    }}
                ]
            }]
        }";
        client.ProcessMessage(json);

        var region = store.GetRegion("g1");
        await Assert.That(region).IsNotNull();
        await Assert.That(region!.Name).IsEqualTo("TestArea");
        // OuterRings populated from the wire coords. First vertex of
        // first ring should be in Leaflet [lat, lon] order.
        await Assert.That(region.OuterRings.Count).IsEqualTo(1);
        var firstVertex = region.OuterRings[0][0];
        await Assert.That(firstVertex[0]).IsEqualTo(50.0);  // lat
        await Assert.That(firstVertex[1]).IsEqualTo(10.0);  // lon
    }

    [Test]
    public async Task DisposeAsync_Unsubscribes_From_SignalkClient_Events()
    {
        // After dispose, a delta on the SignalkClient must NOT mutate
        // the store's cache. Pins the unsubscribe contract: a future
        // refactor that drops the -= calls would silently leak event
        // subscriptions in single-page-app navigation cycles.
        var (client, store, _, _, _, _) = BuildHarness();
        var route = MakeRoute("r1", "Berlin", (13.4, 52.5), (13.5, 52.6));
        client.ProcessMessage(RouteDeltaJson("r1", route));
        await Assert.That(store.GetRoute("r1")).IsNotNull();

        await store.DisposeAsync();

        // Post-dispose delta should be ignored (the store's
        // OnResourceDelta subscription is gone).
        var route2 = MakeRoute("r2", "PostDispose", (1.0, 1.0), (2.0, 2.0));
        client.ProcessMessage(RouteDeltaJson("r2", route2));
        await Assert.That(store.GetRoute("r2")).IsNull();
    }

    [Test]
    public async Task RemoteRegionEdit_WithUnrenderableGeometry_IsFiltered()
    {
        // RegionApi.GetAllAsync drops regions whose Feature.Geometry
        // produces an empty OuterRings list (Point geometry, empty
        // Polygon coords, etc.). HandleRegionDelta must mirror that
        // filter -- otherwise the chart-side region layer hits a null
        // ring on render. Pin the contract so a regression that adds
        // empty-rings regions to the cache surfaces.
        var (client, store, _, _, _, _) = BuildHarness();

        // Point geometry on a region (server-side malformation; SK spec
        // says regions are Polygon/MultiPolygon).
        client.ProcessMessage(@"{
            ""context"":""vessels.self"",
            ""updates"":[{
                ""values"":[
                    {""path"":""resources.regions.g1"",""value"":{
                        ""name"":""BadShape"",
                        ""feature"":{
                            ""type"":""Feature"",
                            ""geometry"":{""type"":""Point"",""coordinates"":[10.0,50.0]}
                        }
                    }}
                ]
            }]
        }");

        await Assert.That(store.GetRegion("g1")).IsNull();
    }

    [Test]
    public async Task RouteDelta_WithUrnFormId_IsCachedUnderFullUrn()
    {
        // signalk-server emits resource ids as urn-form
        // ("urn:mrn:signalk:uuid:<UUID>"). The path-prefix scan in
        // SignalkClient.DispatchResourceUpdates manually splits on the
        // SECOND dot rather than naive Split('.') so colons inside the
        // id segment survive intact. A regression that swaps to
        // Split('.') would shred the urn into pieces; this test pins
        // the contract.
        var (client, store, _, _, _, _) = BuildHarness();

        const string urnId = "urn:mrn:signalk:uuid:7c6a1b00-1234-5678-9abc-deadbeefcafe";
        var route = MakeRoute(urnId, "UrnRoute", (13.4, 52.5), (13.5, 52.6));
        client.ProcessMessage(RouteDeltaJson(urnId, route));

        await Assert.That(store.GetRoute(urnId)).IsNotNull();
        await Assert.That(store.GetRoute(urnId)?.Name).IsEqualTo("UrnRoute");
    }

    [Test]
    public async Task RemoteNoteEdit_WithoutPosition_IsFiltered()
    {
        // SK note resources without a position aren't renderable on the
        // chart. ResourceStore.HandleNoteDelta must filter them, mirroring
        // NoteApi.GetAllAsync's filter -- otherwise a position-less
        // remote-edit would land in the cache and trip a Map.razor
        // marker draw with null lat/lon.
        var (client, store, _, _, _, _) = BuildHarness();

        client.ProcessMessage(@"{
            ""context"":""vessels.self"",
            ""updates"":[{
                ""values"":[
                    {""path"":""resources.notes.n1"",""value"":{""title"":""Float""}}
                ]
            }]
        }");

        await Assert.That(store.GetNote("n1")).IsNull();
    }

    // --- Helpers -----------------------------------------------------

    /// <summary>Robust waypoint-count getter for tests. Returns -1 on
    /// any null link in the route -> feature -> geometry -> coordinates
    /// chain or on a non-Array coordinates value, so a regression that
    /// drops the geometry surfaces as a clear `expected: 3, actual: -1`
    /// assertion failure instead of a NullReferenceException with no
    /// pointer to the broken layer.</summary>
    private static int WpCount(SignalkRoute? r)
    {
        if (r?.Feature?.Geometry?.Coordinates is not { ValueKind: JsonValueKind.Array } coords)
            return -1;
        return coords.GetArrayLength();
    }
}
