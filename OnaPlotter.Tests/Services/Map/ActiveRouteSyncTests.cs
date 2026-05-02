using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Js;
using OnaPlotter.Services.Map;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the diff-and-watchdog logic of <see cref="ActiveRouteSync"/>.
/// The href / next-WP / pointIndex precedence is the highest-value
/// surface to lock in: a regression silently leaves the helm with a
/// bearing line pointing at the wrong leg.
/// </summary>
public class ActiveRouteSyncTests
{
    private sealed class FakeRouteJs : IMapRouteJs
    {
        public List<(double[][] coords, int idx, string id, string name)> ActiveRoutes { get; } = [];
        public int ActiveRouteClears { get; private set; }
        public int CourseLineClears { get; private set; }
        public List<bool> StoppingFlags { get; } = [];
        public List<double?> Ttgs { get; } = [];
        public List<string> Removed { get; } = [];
        public List<(string id, double[][] coords)> Added { get; } = [];

        public Task AddRouteAsync(string id, string? name, double[][] coords)
        {
            Added.Add((id, coords));
            return Task.CompletedTask;
        }

        public Task RemoveRouteAsync(string id)
        {
            Removed.Add(id);
            return Task.CompletedTask;
        }

        public Task SetActiveRouteAsync(double[][] coords, int wpIndex, string routeId, string routeName)
        {
            ActiveRoutes.Add((coords, wpIndex, routeId, routeName));
            return Task.CompletedTask;
        }

        public Task ClearActiveRouteAsync()
        {
            ActiveRouteClears++;
            return Task.CompletedTask;
        }

        public Task SetActiveOverlayHiddenAsync(bool hidden) => Task.CompletedTask;

        public Task SetActiveRouteStoppingAsync(bool stopping)
        {
            StoppingFlags.Add(stopping);
            return Task.CompletedTask;
        }

        public Task SetActiveRouteTtgSecondsAsync(double? seconds)
        {
            Ttgs.Add(seconds);
            return Task.CompletedTask;
        }

        public Task ClearCourseLineAsync()
        {
            CourseLineClears++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRouteApi : IRouteApi
    {
        public Dictionary<string, double[][]?> CoordsByHref { get; } = [];
        public int FetchCount { get; private set; }

        public Task<List<SignalkRoute>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<SignalkRoute>());

        public Task<double[][]?> GetCoordinatesAsync(string href, CancellationToken ct = default)
        {
            FetchCount++;
            return Task.FromResult(CoordsByHref.TryGetValue(href, out var c) ? c : null);
        }

        public Task<ApiResult<string>> SaveAsync(string name, double[][] coordsLatLon, CancellationToken ct = default) =>
            Task.FromResult(ApiResult<string>.Ok(string.Empty));

        public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);

        public Task<ApiResult> UpdateAsync(string id, string name, double[][] coordsLatLon, CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);
    }

    private sealed class TimeBox
    {
        public DateTime Now { get; set; } = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
    }

    /// <summary>
    /// Stub IMapControlsJs that records SetRangeScaleHiddenAsync
    /// calls. ActiveRouteSync uses it to toggle the bottom-centre
    /// range-scale chip when a route activates / deactivates; the
    /// recording lets the test pin that the calls fire at the right
    /// transitions. Other IMapControlsJs methods stay as no-ops --
    /// the sync only touches this one.
    /// </summary>
    private sealed class FakeControlsJs : IMapControlsJs
    {
        public List<bool> RangeScaleHidden { get; } = [];
        public Task SetRangeScaleHiddenAsync(bool hidden)
        {
            RangeScaleHidden.Add(hidden);
            return Task.CompletedTask;
        }
        public Task SetSignalKBaseUrlAsync(string url) => Task.CompletedTask;
        public Task SetMapOrientationAsync(string mode) => Task.CompletedTask;
        public Task SetFollowAsync(bool follow) => Task.CompletedTask;
        public Task ClearLaylinesAsync() => Task.CompletedTask;
        public Task SetNightModeAsync(bool enabled) => Task.CompletedTask;
        public Task SetGuardZoneAsync(double radiusNm, double lookaheadMin, double warningFactor) => Task.CompletedTask;
        public Task SetCogVectorMinutesAsync(double ownMinutes, double aisMinutes) => Task.CompletedTask;
        public Task PanToAsync(double lat, double lon) => Task.CompletedTask;
        public Task ZoomToTrackAsync() => Task.CompletedTask;
        public Task FitBoundsAsync(double minLat, double minLon, double maxLat, double maxLon) => Task.CompletedTask;
        public Task EnableKeyboardShortcutsAsync<T>(Microsoft.JSInterop.DotNetObjectReference<T> dotNetRef) where T : class => Task.CompletedTask;
        public Task DisableKeyboardShortcutsAsync() => Task.CompletedTask;
        public Task ApplyFrameAsync(object frame) => Task.CompletedTask;
        public Task SetMobAsync(double lat, double lon) => Task.CompletedTask;
        public Task ClearMobAsync() => Task.CompletedTask;
        public Task ClearCurrentArrowAsync() => Task.CompletedTask;
        public Task<double[]?> GetMapCenterAsync() => Task.FromResult<double[]?>(null);
    }

    private static (
        ActiveRouteSync sync,
        FakeRouteJs js,
        FakeRouteApi api,
        FakeControlsJs controls,
        List<string> warnings,
        TimeBox time)
    NewSync(bool connected = true)
    {
        var js = new FakeRouteJs();
        var api = new FakeRouteApi();
        var controls = new FakeControlsJs();
        var time = new TimeBox();
        var warnings = new List<string>();
        var sync = new ActiveRouteSync(
            js, controls, api,
            isSignalKConnected: () => connected,
            stopTimeoutWarning: msg => warnings.Add(msg),
            utcNow: () => time.Get());
        return (sync, js, api, controls, warnings, time);
    }

    private static NavigationData ActiveRoute(string href, double nextLat, double nextLon, int? pointIndex = 0)
    {
        var data = new NavigationData();
        data.ApplyString("navigation.course.activeRoute.href", href);
        data.ApplyString("navigation.course.activeRoute.name", "Test Route");
        data.ApplyCourseNextPointPosition(nextLat, nextLon);
        if (pointIndex is int idx) data.Apply("navigation.course.activeRoute.pointIndex", idx);
        return data;
    }

    [Test]
    public async Task First_Activation_Fetches_Geometry_And_Draws()
    {
        // Helm activates route; controller fetches the coords once,
        // resolves the leg index, and pushes to JS.
        var (sync, js, api, _, _, _) = NewSync();
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3], [54.7, 11.4]];
        var data = ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1);

        await sync.SyncAsync(data, new HashSet<string>(), []);

        await Assert.That(api.FetchCount).IsEqualTo(1);
        await Assert.That(js.ActiveRoutes.Count).IsEqualTo(1);
        await Assert.That(js.ActiveRoutes[0].id).IsEqualTo("r1");
        await Assert.That(js.ActiveRoutes[0].idx).IsEqualTo(1);
        await Assert.That(sync.ActiveRouteDistanceTotal).IsNotNull();
    }

    [Test]
    public async Task Repeat_Sync_Same_Href_And_Wp_No_Refetch_No_Redraw()
    {
        // Diff state: once drawn, an unchanged tick is a full no-op.
        // The diff gate matches the byte-equivalent behaviour of the
        // pre-extraction Map.razor SyncActiveRouteAsync.
        var (sync, js, api, _, _, _) = NewSync();
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3]];
        var data = ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1);

        await sync.SyncAsync(data, new HashSet<string>(), []);
        await sync.SyncAsync(data, new HashSet<string>(), []);

        await Assert.That(api.FetchCount).IsEqualTo(1);
        await Assert.That(js.ActiveRoutes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Next_Wp_Change_Redraws_Without_Refetch()
    {
        // Leg advance: same href, different next-WP coords. Reuses
        // cached coords (no second HTTP) but redraws with the new idx.
        var (sync, js, api, _, _, _) = NewSync();
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3], [54.7, 11.4]];

        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), new HashSet<string>(), []);
        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.7, 11.4, pointIndex: 2), new HashSet<string>(), []);

        await Assert.That(api.FetchCount).IsEqualTo(1);     // cached
        await Assert.That(js.ActiveRoutes.Count).IsEqualTo(2);
        await Assert.That(js.ActiveRoutes[1].idx).IsEqualTo(2);
    }

    [Test]
    public async Task Point_Index_Change_Alone_Triggers_Redraw()
    {
        // pointIndex landing before nextPoint.position on page reload:
        // the redraw mustn't wait for nextPoint.position. A pointIndex
        // delta with the same next-WP coords should still fire.
        var (sync, js, api, _, _, _) = NewSync();
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3], [54.7, 11.4]];

        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), new HashSet<string>(), []);
        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 2), new HashSet<string>(), []);

        await Assert.That(js.ActiveRoutes.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Deactivation_Clears_Active_Overlay()
    {
        // Route deactivated (StopNavigation, server clears href);
        // controller clears the polyline + course line.
        var (sync, js, api, _, _, _) = NewSync();
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3]];
        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), new HashSet<string>(), []);

        // Now no active route.
        await sync.SyncAsync(new NavigationData(), new HashSet<string>(), []);

        await Assert.That(js.ActiveRouteClears).IsEqualTo(1);
        await Assert.That(js.CourseLineClears).IsEqualTo(1);
        await Assert.That(sync.ActiveRouteDistanceTotal).IsNull();
    }

    [Test]
    public async Task Force_Refresh_Refetches_Geometry()
    {
        // In-place edit of the active route: href hasn't changed but
        // geometry has. force=true bypasses the gate.
        var (sync, js, api, _, _, _) = NewSync();
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3]];
        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), new HashSet<string>(), []);
        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), new HashSet<string>(), [], force: true);

        await Assert.That(api.FetchCount).IsEqualTo(2);
    }

    [Test]
    public async Task Active_Route_Suppresses_Regular_Polyline_When_Enabled()
    {
        // The same id as a saved-route polyline + a brand-new active
        // route: controller calls removeRoute on the regular polyline
        // so the active overlay doesn't double-stroke.
        var (sync, js, api, _, _, _) = NewSync();
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3]];
        var enabled = new HashSet<string> { "r1" };

        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), enabled, []);

        await Assert.That(js.Removed).IsEquivalentTo(["r1"]);
        await Assert.That(sync.ActiveRouteRegularSuppressed).IsEqualTo("r1");
    }

    [Test]
    public async Task Switching_Active_Routes_Restores_Previous_Polyline()
    {
        // Active changes from r1 to r2; r1's regular polyline gets
        // restored via the page callback so the helm sees it again.
        var (sync, js, api, _, _, _) = NewSync();
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3]];
        api.CoordsByHref["/resources/routes/r2"] = [[55.0, 12.0], [55.1, 12.1]];
        var enabled = new HashSet<string> { "r1", "r2" };
        var available = new List<SignalkRoute>
        {
            new() { Id = "r1", Name = "First" },
            new() { Id = "r2", Name = "Second" },
        };
        var restored = new List<string>();
        sync.OnRestoreSuppressedRoute = id => { restored.Add(id); return Task.CompletedTask; };

        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), enabled, available);
        await sync.SyncAsync(ActiveRoute("/resources/routes/r2", 55.1, 12.1, pointIndex: 1), enabled, available);

        await Assert.That(restored).IsEquivalentTo(["r1"]);
        await Assert.That(sync.ActiveRouteRegularSuppressed).IsEqualTo("r2");
    }

    [Test]
    public async Task Stop_Watchdog_Fires_After_Timeout()
    {
        // Stop tapped; SK delta hasn't cleared href within 5 s; warn +
        // un-dim the polyline.
        var (sync, js, api, _, warnings, time) = NewSync();
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3]];
        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), new HashSet<string>(), []);
        sync.MarkCourseStopPending();

        time.Now = time.Now.AddSeconds(6);
        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), new HashSet<string>(), []);

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0]).Contains("Stop navigation");
        await Assert.That(js.StoppingFlags).IsEquivalentTo([false]);
        await Assert.That(sync.CourseStopPending).IsFalse();
    }

    [Test]
    public async Task Stop_Watchdog_Suppressed_While_Disconnected()
    {
        // Same as anchor watchdog: re-arm each tick while WS is down.
        var (sync, js, api, _, warnings, time) = NewSync(connected: false);
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3]];
        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), new HashSet<string>(), []);
        sync.MarkCourseStopPending();

        time.Now = time.Now.AddSeconds(20);
        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), new HashSet<string>(), []);

        await Assert.That(warnings).IsEmpty();
    }

    [Test]
    public async Task Stop_Pending_Cleared_When_Server_Confirms()
    {
        // Helm tapped Stop; SK delta clears href; pending flag follows
        // truth and the watchdog stays quiet.
        var (sync, js, api, _, warnings, time) = NewSync();
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3]];
        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), new HashSet<string>(), []);
        sync.MarkCourseStopPending();
        await sync.SyncAsync(new NavigationData(), new HashSet<string>(), []);

        time.Now = time.Now.AddSeconds(20);
        await sync.SyncAsync(new NavigationData(), new HashSet<string>(), []);

        await Assert.That(warnings).IsEmpty();
        await Assert.That(sync.CourseStopPending).IsFalse();
    }

    [Test]
    public async Task Invalidate_Href_Forces_Refetch_On_Next_Sync()
    {
        // Page-side "force redraw NOW" hook: invalidate cached href,
        // next sync refetches even though the SK href is unchanged.
        var (sync, js, api, _, _, _) = NewSync();
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3]];
        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), new HashSet<string>(), []);

        sync.InvalidateActiveRouteHref();
        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), new HashSet<string>(), []);

        await Assert.That(api.FetchCount).IsEqualTo(2);
    }

    [Test]
    public async Task On_Active_Overlay_Cleared_Callback_Fires()
    {
        // Page wires this to the frame builder's CourseLineDrawn flag
        // so the next frame draws a fresh course line. Verify the hook.
        var (sync, js, api, _, _, _) = NewSync();
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3]];
        await sync.SyncAsync(ActiveRoute("/resources/routes/r1", 54.6, 11.3, pointIndex: 1), new HashSet<string>(), []);
        bool called = false;
        sync.OnActiveOverlayCleared = () => called = true;

        await sync.SyncAsync(new NavigationData(), new HashSet<string>(), []);

        await Assert.That(called).IsTrue();
    }

    [Test]
    public async Task Route_Name_Falls_Back_To_AvailableRoutes_Name()
    {
        // Active route name from delta is null; controller looks up
        // the human-readable name in availableRoutes so the popup
        // never reads "Route abc-12".
        var (sync, js, api, _, _, _) = NewSync();
        api.CoordsByHref["/resources/routes/r1"] = [[54.5, 11.2], [54.6, 11.3]];
        var available = new List<SignalkRoute> { new() { Id = "r1", Name = "Friday Sail" } };
        var data = new NavigationData();
        // Deliberately apply only the href (no name) and the next-point coords.
        data.ApplyString("navigation.course.activeRoute.href", "/resources/routes/r1");
        data.ApplyCourseNextPointPosition(54.6, 11.3);
        data.Apply("navigation.course.activeRoute.pointIndex", 1);

        await sync.SyncAsync(data, new HashSet<string>(), available);

        await Assert.That(js.ActiveRoutes[0].name).IsEqualTo("Friday Sail");
    }
}
