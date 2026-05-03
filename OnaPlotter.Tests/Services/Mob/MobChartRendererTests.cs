using OnaPlotter.Services.Js;
using OnaPlotter.Services.Mob;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Tests.Services.Mob;

/// <summary>
/// Pins the MOB chart-marker rendering: store mutations translate
/// to the right setMob / clearMob JS calls, the v1 single-marker
/// limit is honoured, position-null entries paint nothing, and
/// AttachJs / DetachJs lifecycle works regardless of whether MOBs
/// already exist in the store.
/// </summary>
public class MobChartRendererTests
{
    private const string MobPathPrefix = "notifications.mob.";

    private static (MobChartRenderer renderer, ServerNotificationStore store, FakeJs js) NewFixture()
    {
        var store = new ServerNotificationStore();
        var renderer = new MobChartRenderer(store);
        var js = new FakeJs();
        return (renderer, store, js);
    }

    private static NotificationStatus EmergencyStatus =>
        new(Silenced: false, Acknowledged: false,
            CanSilence: false, CanAcknowledge: true, CanClear: true);

    [Test]
    public async Task Path_Change_Triggers_SetMob_When_Position_Present()
    {
        // Happy path: a MOB lands in the store; the renderer emits
        // exactly one setMob call.
        var (r, store, js) = NewFixture();
        r.AttachJs(js);

        store.Apply(MobPathPrefix + "foo", "emergency", "MOB",
            id: "foo", status: EmergencyStatus,
            latitude: 47.5, longitude: 8.5);

        await Assert.That(js.SetMobCalls.Count).IsEqualTo(1);
        await Assert.That(js.SetMobCalls[0].lat).IsEqualTo(47.5);
        await Assert.That(js.SetMobCalls[0].lon).IsEqualTo(8.5);
        await Assert.That(js.ClearMobCount).IsEqualTo(0);
    }

    [Test]
    public async Task Path_Change_Skips_When_Position_Null()
    {
        // SK MOB notification position is allowed to be null. The
        // chart-marker layer can't draw a marker without coords, so
        // the renderer is a no-op for that case (banner still
        // fires via the alarm pipeline).
        var (r, store, js) = NewFixture();
        r.AttachJs(js);

        store.Apply(MobPathPrefix + "no-fix", "emergency", "MOB",
            id: "no-fix", status: EmergencyStatus,
            latitude: null, longitude: null);

        await Assert.That(js.SetMobCalls.Count).IsEqualTo(0);
        await Assert.That(js.ClearMobCount).IsEqualTo(0);
    }

    [Test]
    public async Task Last_Mob_Cleared_Triggers_ClearMob()
    {
        // Single MOB rendered, then store clears it -> ClearMobAsync
        // fires; the marker comes off the chart.
        var (r, store, js) = NewFixture();
        r.AttachJs(js);
        store.Apply(MobPathPrefix + "foo", "emergency", "MOB",
            id: "foo", status: EmergencyStatus,
            latitude: 47.5, longitude: 8.5);

        store.Clear(MobPathPrefix + "foo");

        await Assert.That(js.ClearMobCount).IsEqualTo(1);
    }

    [Test]
    public async Task Identical_Repeat_Apply_Does_Not_Re_Push_SetMob()
    {
        // OnPathChanged fires on every Apply, including a status-
        // only update. The renderer must not re-push setMob if the
        // path + coords haven't changed -- otherwise the marker
        // re-pulses on every server status tick.
        var (r, store, js) = NewFixture();
        r.AttachJs(js);
        store.Apply(MobPathPrefix + "foo", "emergency", "MOB",
            id: "foo", status: EmergencyStatus,
            latitude: 47.5, longitude: 8.5);
        await Assert.That(js.SetMobCalls.Count).IsEqualTo(1);

        // Re-apply same coords (status block changes but coords
        // identical).
        store.Apply(MobPathPrefix + "foo", "emergency", "MOB acknowledged",
            id: "foo", status: EmergencyStatus,
            latitude: 47.5, longitude: 8.5);

        await Assert.That(js.SetMobCalls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Position_Update_Re_Pushes_SetMob()
    {
        // If the MOB's position field changes (rare but possible if
        // the SK server enriches with a later fix), the renderer
        // re-pushes so the marker tracks the new coords.
        var (r, store, js) = NewFixture();
        r.AttachJs(js);
        store.Apply(MobPathPrefix + "foo", "emergency", "MOB",
            id: "foo", status: EmergencyStatus,
            latitude: 47.5, longitude: 8.5);

        store.Apply(MobPathPrefix + "foo", "emergency", "MOB",
            id: "foo", status: EmergencyStatus,
            latitude: 47.6, longitude: 8.6);

        await Assert.That(js.SetMobCalls.Count).IsEqualTo(2);
        await Assert.That(js.SetMobCalls[1].lat).IsEqualTo(47.6);
    }

    [Test]
    public async Task Multiple_Mobs_Renders_Only_First_V1_Single_Marker_Limit()
    {
        // v1 documented limit: the JS layer is single-marker, so
        // the renderer paints the first active MOB only. Banner
        // pipeline still surfaces both via the alarm stack.
        var (r, store, js) = NewFixture();
        r.AttachJs(js);

        store.Apply(MobPathPrefix + "first", "emergency", "MOB 1",
            id: "first", status: EmergencyStatus,
            latitude: 47.5, longitude: 8.5);
        store.Apply(MobPathPrefix + "second", "emergency", "MOB 2",
            id: "second", status: EmergencyStatus,
            latitude: 48.0, longitude: 9.0);

        // The first MOB stays rendered; the second arrival doesn't
        // re-push (the rendered path matches the first).
        await Assert.That(js.SetMobCalls.Count).IsEqualTo(1);
        await Assert.That(js.SetMobCalls[0].lat).IsEqualTo(47.5);
    }

    [Test]
    public async Task AttachJs_Renders_Existing_Active_Mob()
    {
        // A MOB landed in the store BEFORE the page mounted (the JS
        // bridge wasn't ready yet). When AttachJs runs at mount
        // time, the renderer re-syncs and paints the existing MOB.
        var (r, store, js) = NewFixture();
        store.Apply(MobPathPrefix + "early", "emergency", "MOB",
            id: "early", status: EmergencyStatus,
            latitude: 47.5, longitude: 8.5);

        // No JS yet -- nothing painted.
        await Assert.That(js.SetMobCalls.Count).IsEqualTo(0);

        r.AttachJs(js);

        await Assert.That(js.SetMobCalls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DetachJs_Stops_Rendering_But_AttachJs_Repaints()
    {
        // Page navigates away (DetachJs) then back (AttachJs). The
        // renderer's subscription stays live across navigation; the
        // post-attach re-sync repaints the existing MOB.
        var (r, store, js) = NewFixture();
        r.AttachJs(js);
        store.Apply(MobPathPrefix + "foo", "emergency", "MOB",
            id: "foo", status: EmergencyStatus,
            latitude: 47.5, longitude: 8.5);
        await Assert.That(js.SetMobCalls.Count).IsEqualTo(1);

        r.DetachJs();

        // While detached, store mutations don't reach the (null) bridge.
        store.Apply(MobPathPrefix + "foo", "emergency", "MOB updated",
            id: "foo", status: EmergencyStatus,
            latitude: 47.6, longitude: 8.6);
        await Assert.That(js.SetMobCalls.Count).IsEqualTo(1);

        // Re-attach -> the renderer re-syncs + repaints.
        r.AttachJs(js);
        await Assert.That(js.SetMobCalls.Count).IsEqualTo(2);
        await Assert.That(js.SetMobCalls[1].lat).IsEqualTo(47.6);
    }

    /// <summary>Fake IMapControlsJs: records SetMob / ClearMob
    /// calls. Other interface methods are no-ops -- the renderer
    /// only uses these two.</summary>
    private sealed class FakeJs : IMapControlsJs
    {
        public List<(double lat, double lon, string? createdAtIso, string? selfMmsi)> SetMobCalls { get; } = [];
        public int ClearMobCount { get; private set; }

        public Task SetMobAsync(double lat, double lon, string? createdAtIso, string? selfMmsi)
        {
            SetMobCalls.Add((lat, lon, createdAtIso, selfMmsi));
            return Task.CompletedTask;
        }
        public Task ClearMobAsync()
        {
            ClearMobCount++;
            return Task.CompletedTask;
        }

        // Unused stubs.
        public Task SetSignalKBaseUrlAsync(string url) => Task.CompletedTask;
        public Task SetMapOrientationAsync(string mode) => Task.CompletedTask;
        public Task SetFollowAsync(bool follow) => Task.CompletedTask;
        public Task ClearLaylinesAsync() => Task.CompletedTask;
        public Task SetShipLinesVisibleAsync(bool enabled) => Task.CompletedTask;
        public Task SetRadarRangeRingsAsync(bool enabled, int count) => Task.CompletedTask;
        public Task SetNightModeAsync(bool enabled) => Task.CompletedTask;
        public Task SetGuardZoneAsync(double radiusNm, double lookaheadMin, double warningFactor) => Task.CompletedTask;
        public Task SetCogVectorMinutesAsync(double ownMinutes, double aisMinutes) => Task.CompletedTask;
        public Task PanToAsync(double lat, double lon) => Task.CompletedTask;
        public Task ZoomToTrackAsync() => Task.CompletedTask;
        public Task FitBoundsAsync(double minLat, double minLon, double maxLat, double maxLon) => Task.CompletedTask;
        public Task EnableKeyboardShortcutsAsync<T>(Microsoft.JSInterop.DotNetObjectReference<T> dotNetRef) where T : class => Task.CompletedTask;
        public Task DisableKeyboardShortcutsAsync() => Task.CompletedTask;
        public Task ApplyFrameAsync(object frame) => Task.CompletedTask;
        public Task SetRangeScaleHiddenAsync(bool hidden) => Task.CompletedTask;
        public Task ClearCurrentArrowAsync() => Task.CompletedTask;
        public Task<double[]?> GetMapCenterAsync() => Task.FromResult<double[]?>(null);
    }
}
