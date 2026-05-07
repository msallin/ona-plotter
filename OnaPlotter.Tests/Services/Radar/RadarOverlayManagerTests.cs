using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Radar;

namespace OnaPlotter.Tests.Services.Radar;

/// <summary>
/// Integration tests for the manager's orchestration loop - the glue
/// between <see cref="RadarOverlayAutoToggle.Decide"/>, the host
/// (Start/Stop/UpdateRange), and the per-radar capability cache.
/// The pure decision rule has its own unit tests; these pin the
/// behaviour that emerges across multiple polls (sticky-off survives
/// a transmit -> standby -> transmit cycle, idempotency on repeat
/// calls, range diff suppression).
///
/// Uses hand-rolled fakes for the host + radar API so no JS interop
/// or HTTP round-trip happens. Test assertions inspect the captured
/// host calls.
/// </summary>
public class RadarOverlayManagerTests
{
    // --- fakes --------------------------------------------------------

    private sealed class FakeHost : IRadarOverlayHost
    {
        public List<RadarOverlayStartConfig> Started { get; } = [];
        public List<string> Stopped { get; } = [];
        public List<(string id, int range)> Ranges { get; } = [];

        /// <summary>When set, StartOverlayAsync throws on next call -
        /// drives the "host failed; manager surfaces toast" path.</summary>
        public Exception? NextStartError { get; set; }

        public Task StartOverlayAsync(RadarOverlayStartConfig cfg, CancellationToken ct = default)
        {
            if (NextStartError is not null)
            {
                var e = NextStartError;
                NextStartError = null;
                throw e;
            }
            Started.Add(cfg);
            return Task.CompletedTask;
        }

        public Task StopOverlayAsync(string radarId, CancellationToken ct = default)
        {
            Stopped.Add(radarId);
            return Task.CompletedTask;
        }

        public Task UpdateRangeAsync(string radarId, int range, CancellationToken ct = default)
        {
            Ranges.Add((radarId, range));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRadarApi : IRadarApi
    {
        public Dictionary<string, RadarCapabilities?> CapsByRadar { get; } = [];
        public int CapabilityFetches { get; private set; }

        public Task<IReadOnlyList<RadarInfo>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RadarInfo>>([]);

        public Task<RadarCapabilities?> GetCapabilitiesAsync(string radarId, CancellationToken ct = default)
        {
            CapabilityFetches++;
            return Task.FromResult(CapsByRadar.TryGetValue(radarId, out var c) ? c : null);
        }

        public Task<Dictionary<string, ControlValue>?> GetControlsAsync(string radarId, CancellationToken ct = default) =>
            Task.FromResult<Dictionary<string, ControlValue>?>(null);

        public Task<ApiResult> SetControlAsync(string radarId, string controlId, ControlValue value, CancellationToken ct = default) =>
            Task.FromResult(ApiResult.Ok);
    }

    private sealed class FakeBaseUrl : ISignalKBaseUrl
    {
        public string BaseUrl => "https://test.local:443";
        public Uri StreamUri(string subscribe = "none") => new("wss://test.local:443/signalk/v1/stream");
        public string Combine(string path) => BaseUrl + path;
    }

    private static RadarInfo Radar(string id, string status = "transmit", int? range = 1852, string? spokeUrl = null) => new()
    {
        Id = id,
        Name = $"R-{id}",
        Status = status,
        SpokesPerRevolution = 2048,
        MaxSpokeLength = 1024,
        Range = range,
        SpokeDataUrl = spokeUrl,
    };

    private static (RadarOverlayManager mgr, FakeHost host, FakeRadarApi api) NewManager()
    {
        var host = new FakeHost();
        var api = new FakeRadarApi();
        var mgr = new RadarOverlayManager(api, host, new FakeBaseUrl());
        return (mgr, host, api);
    }

    // --- TEST-002: integration ---------------------------------------

    [Test]
    public async Task Transmitting_Radar_Auto_Enables_Overlay_On_First_Poll()
    {
        // Helm hits Transmit on the radar; on the next radar-list refresh
        // the overlay should auto-open without a second click. Verifies
        // the loop end-to-end (Decide -> EnableAsync -> host.StartOverlayAsync).
        var (mgr, host, _) = NewManager();
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "transmit")]);

        await Assert.That(host.Started.Count).IsEqualTo(1);
        await Assert.That(host.Started[0].RadarId).IsEqualTo("r1");
        await Assert.That(mgr.EnabledRadarIds.Contains("r1")).IsTrue();
    }

    [Test]
    public async Task Standby_Tears_Down_Active_Overlay_On_Next_Poll()
    {
        // Symmetric: a radar that was transmitting goes to standby; the
        // empty-canvas overlay gets torn down so we don't reconnect-
        // loop on a no-data WebSocket.
        var (mgr, host, _) = NewManager();
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "transmit")]);
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "standby")]);

        await Assert.That(host.Stopped).IsEquivalentTo(["r1"]);
        await Assert.That(mgr.EnabledRadarIds.Contains("r1")).IsFalse();
    }

    [Test]
    public async Task Sticky_Off_Survives_Standby_Transmit_Cycle()
    {
        // The whole point of the sticky-off bit. After a deliberate
        // untick, a transmit -> standby -> transmit cycle must NOT
        // re-enable the overlay; the user has to re-tick to clear
        // the preference.
        var (mgr, host, _) = NewManager();

        // Initial transmit auto-enables.
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "transmit")]);
        await Assert.That(host.Started.Count).IsEqualTo(1);

        // User unticks while transmitting.
        await mgr.OnUserToggleAsync(Radar("r1", "transmit"), enabled: false);
        await Assert.That(host.Stopped).IsEquivalentTo(["r1"]);
        await Assert.That(mgr.IsUserDisabled("r1")).IsTrue();

        // Cycle the radar through standby and back to transmit. The
        // overlay must STAY off because of the sticky preference.
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "standby")]);
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "transmit")]);
        await Assert.That(host.Started.Count).IsEqualTo(1);   // no new start
        await Assert.That(mgr.EnabledRadarIds.Contains("r1")).IsFalse();
    }

    [Test]
    public async Task Re_Tick_Clears_Sticky_Off_And_Re_Enables()
    {
        // Counterpart: re-ticking the layer checkbox clears the sticky
        // bit so future auto-toggle passes work again.
        var (mgr, host, _) = NewManager();
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "transmit")]);
        await mgr.OnUserToggleAsync(Radar("r1", "transmit"), enabled: false);
        await mgr.OnUserToggleAsync(Radar("r1", "transmit"), enabled: true);

        await Assert.That(mgr.IsUserDisabled("r1")).IsFalse();
        await Assert.That(host.Started.Count).IsEqualTo(2);   // initial + re-enable
        await Assert.That(mgr.EnabledRadarIds.Contains("r1")).IsTrue();
    }

    [Test]
    public async Task Multi_Radar_Independent_State_Per_Id()
    {
        // Two radars in transmit; user disables one. Other stays on.
        // Pins that the per-radar sticky bit is independent.
        var (mgr, host, _) = NewManager();
        await mgr.OnRadarListUpdatedAsync([Radar("a", "transmit"), Radar("b", "transmit")]);
        await Assert.That(host.Started.Count).IsEqualTo(2);

        await mgr.OnUserToggleAsync(Radar("a", "transmit"), enabled: false);
        await Assert.That(mgr.EnabledRadarIds.Contains("a")).IsFalse();
        await Assert.That(mgr.EnabledRadarIds.Contains("b")).IsTrue();
    }

    [Test]
    public async Task Range_Update_Pushed_Only_When_Changed()
    {
        // The JS layer recomputes bounds + reposition on every range
        // update; pushing on every poll would thrash. PushRange must
        // diff per radar against the last-known value.
        var (mgr, host, _) = NewManager();
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "transmit", range: 1852)]);
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "transmit", range: 1852)]);  // unchanged
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "transmit", range: 3704)]);  // changed

        await Assert.That(host.Ranges.Count).IsEqualTo(1);
        await Assert.That(host.Ranges[0]).IsEqualTo(("r1", 3704));
    }

    [Test]
    public async Task Range_Update_Skipped_When_Overlay_Off()
    {
        // No reason to push range updates to an overlay that isn't
        // open; the JS layer wouldn't know what to do with them.
        var (mgr, host, _) = NewManager();
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "standby", range: 1852)]);
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "standby", range: 3704)]);

        await Assert.That(host.Ranges).IsEmpty();
    }

    // --- TEST-003: idempotency ---------------------------------------

    [Test]
    public async Task EnableAsync_Idempotent_Across_Repeated_Polls()
    {
        // The auto-toggle pass runs every poll; if Enable wasn't
        // idempotent we'd open a new WebSocket every cycle, leaking
        // resources and confusing the JS layer's per-radar registry.
        var (mgr, host, _) = NewManager();
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "transmit")]);
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "transmit")]);
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "transmit")]);

        await Assert.That(host.Started.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DisableAsync_Idempotent_Across_Repeated_Polls()
    {
        // Symmetric: a radar that was never transmitting (so overlay
        // never on) shouldn't trigger Stop calls.
        var (mgr, host, _) = NewManager();
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "standby")]);
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "standby")]);

        await Assert.That(host.Stopped).IsEmpty();
    }

    [Test]
    public async Task Capability_Fetched_At_Most_Once_Per_Radar()
    {
        // Capabilities are spec-stable per radar; the prefetch must
        // skip ids it's already seen so the polling path doesn't
        // hammer the server.
        var (mgr, _, api) = NewManager();
        await mgr.OnRadarListUpdatedAsync([Radar("r1"), Radar("r2")]);
        await mgr.OnRadarListUpdatedAsync([Radar("r1"), Radar("r2")]);
        await mgr.OnRadarListUpdatedAsync([Radar("r1"), Radar("r2")]);

        await Assert.That(api.CapabilityFetches).IsEqualTo(2);
    }

    // --- start failure surfaces via OnError --------------------------

    [Test]
    public async Task Start_Failure_Surfaces_Via_OnError_Callback()
    {
        // RadarOverlayException from the host must reach OnError so
        // the page can toast it. Crucially the manager must NOT mark
        // the radar as enabled in that case - otherwise idempotency
        // would block re-enable attempts.
        var (mgr, host, _) = NewManager();
        var errors = new List<string>();
        mgr.OnError = errors.Add;
        host.NextStartError = new RadarOverlayException("boom");

        await mgr.OnRadarListUpdatedAsync([Radar("r1", "transmit")]);

        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("boom");
        await Assert.That(mgr.EnabledRadarIds.Contains("r1")).IsFalse();
    }

    // --- spoke URL guard --------------------------------------------

    [Test]
    public async Task Cross_Origin_SpokeDataUrl_Falls_Back_To_Same_Origin_Builder()
    {
        // A hostile / misconfigured plugin shipping spokeDataUrl that
        // points at another host gets rejected; the canonical SK-
        // proxied URL is used instead so the browser only opens
        // WebSockets back at our own origin.
        var (mgr, host, _) = NewManager();
        var hostile = Radar("r1", "transmit", spokeUrl: "wss://attacker.example.com/x");
        await mgr.OnRadarListUpdatedAsync([hostile]);

        await Assert.That(host.Started.Count).IsEqualTo(1);
        await Assert.That(host.Started[0].SpokeDataUrl).Contains("test.local");
        await Assert.That(host.Started[0].SpokeDataUrl).DoesNotContain("attacker");
    }

    [Test]
    public async Task Same_Origin_SpokeDataUrl_Honoured()
    {
        var (mgr, host, _) = NewManager();
        var ok = Radar("r1", "transmit",
            spokeUrl: "wss://test.local:443/signalk/v2/api/vessels/self/radars/r1/stream");
        await mgr.OnRadarListUpdatedAsync([ok]);

        await Assert.That(host.Started.Count).IsEqualTo(1);
        await Assert.That(host.Started[0].SpokeDataUrl).IsEqualTo(ok.SpokeDataUrl);
    }

    // --- bug 1: stable layer order -----------------------------------

    [Test]
    public async Task Radars_Sorted_By_Id_So_Layer_Order_Is_Stable()
    {
        // Mayara has been observed shipping [B, A] then [A, B] across
        // calls; reordering the rows under the helm's finger as they
        // tap is bad UX. Pin alphabetical ordinal sort.
        var (mgr, _, _) = NewManager();
        await mgr.OnRadarListUpdatedAsync([Radar("b"), Radar("a"), Radar("c")]);

        var ids = mgr.Radars.Select(r => r.Id).ToList();
        await Assert.That(ids).IsEquivalentTo(["a", "b", "c"]);
    }

    [Test]
    public async Task Sort_Is_Stable_Across_Repeated_Polls_With_Reordered_Server_Response()
    {
        // Same set, different server order on each poll. Manager
        // output stays identical so Blazor doesn't re-shuffle the
        // rendered rows.
        var (mgr, _, _) = NewManager();
        await mgr.OnRadarListUpdatedAsync([Radar("nav0231B"), Radar("nav0231A")]);
        var first = mgr.Radars.Select(r => r.Id).ToList();
        await mgr.OnRadarListUpdatedAsync([Radar("nav0231A"), Radar("nav0231B")]);
        var second = mgr.Radars.Select(r => r.Id).ToList();

        await Assert.That(first).IsEquivalentTo(second);
        await Assert.That(first).IsEquivalentTo(["nav0231A", "nav0231B"]);
    }

    // --- bug 4: reference-equality drives child re-render ------------

    [Test]
    public async Task EnabledRadarIds_Reference_Changes_When_Set_Mutates()
    {
        // Blazor's child-component change detection is reference-
        // equality on parameter values. When the manager mutated the
        // backing HashSet in-place, LayersPanel kept showing stale
        // checkbox state until the user closed and reopened the
        // panel (forcing a remount). Pin that the property returns a
        // different reference after a mutation so re-render fires.
        var (mgr, _, _) = NewManager();
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "standby")]);

        var refBefore = mgr.EnabledRadarIds;

        // User-toggle on a standby radar adds it to enabled (the
        // host call goes through; the standby status doesn't block
        // a manual enable).
        await mgr.OnUserToggleAsync(Radar("r1", "standby"), enabled: true);

        var refAfter = mgr.EnabledRadarIds;
        await Assert.That(ReferenceEquals(refBefore, refAfter)).IsFalse();
        await Assert.That(refAfter.Contains("r1")).IsTrue();
    }

    [Test]
    public async Task EnabledRadarIds_Reference_Changes_On_Auto_Enable()
    {
        // Same pin for the auto-toggle path - arguably the more
        // important case because the original bug surfaced after
        // hitting Transmit (auto-enable) rather than the layer
        // checkbox (user-toggle).
        var (mgr, _, _) = NewManager();
        await mgr.OnRadarListUpdatedAsync([Radar("r1", "standby")]);
        var refBefore = mgr.EnabledRadarIds;

        await mgr.OnRadarListUpdatedAsync([Radar("r1", "transmit")]);

        var refAfter = mgr.EnabledRadarIds;
        await Assert.That(ReferenceEquals(refBefore, refAfter)).IsFalse();
        await Assert.That(refAfter.Contains("r1")).IsTrue();
    }

    [Test]
    public async Task Capabilities_Reference_Changes_When_New_Caps_Cached()
    {
        // Same reference-equality concern for the dropdown's range
        // options binding. After a poll that learns about a new
        // radar, Capabilities must surface as a new reference so
        // RadarsSection re-renders with the actual validValues.
        var (mgr, _, api) = NewManager();
        api.CapsByRadar["r1"] = new RadarCapabilities
        {
            SupportedRanges = [500, 1000],
        };
        var refBefore = mgr.Capabilities;
        await mgr.OnRadarListUpdatedAsync([Radar("r1")]);
        var refAfter = mgr.Capabilities;

        await Assert.That(ReferenceEquals(refBefore, refAfter)).IsFalse();
        await Assert.That(refAfter.ContainsKey("r1")).IsTrue();
    }
}
