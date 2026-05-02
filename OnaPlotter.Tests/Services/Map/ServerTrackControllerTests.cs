using OnaPlotter.Services.Api;
using OnaPlotter.Services.Js;
using OnaPlotter.Services.Map;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the server-track interop contract: fetch + push on enable
/// or duration change, no re-fetch on within-bounds toggle, "all"
/// maps to a 100-year ISO duration, empty result auto-disables.
/// </summary>
public class ServerTrackControllerTests
{
    private sealed class FakeOverlaysJs : IMapOverlaysJs
    {
        public List<(double[][] coords, bool within)> ServerSets { get; } = [];
        public int Clears { get; private set; }
        public List<bool> ClipFlags { get; } = [];

        public Task AddChartLayerAsync(string id, string tileUrl, int minZoom, int maxZoom, double opacity, double[]? bounds, int upscaleLevels, string attribution) => Task.CompletedTask;
        public Task RemoveChartLayerAsync(string id) => Task.CompletedTask;
        public Task SetChartLayerOrderAsync(string[] orderedIds) => Task.CompletedTask;
        public Task SetWeatherOverlayAsync(string tileUrl, double opacity) => Task.CompletedTask;
        public Task SetWeatherOverlayOpacityAsync(double opacity) => Task.CompletedTask;
        public Task ClearWeatherOverlayAsync() => Task.CompletedTask;
        public Task SetColoredTrackAsync(double[][] points) => Task.CompletedTask;

        public Task SetServerTrackAsync(double[][] coords, bool clipToBounds)
        {
            ServerSets.Add((coords, clipToBounds));
            return Task.CompletedTask;
        }

        public Task SetServerTrackClipToBoundsAsync(bool enabled)
        {
            ClipFlags.Add(enabled);
            return Task.CompletedTask;
        }

        public Task ClearServerTrackAsync()
        {
            Clears++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTrackApi : ITrackApi
    {
        public List<(string span, string resolution)> Calls { get; } = [];
        public OnaPlotter.Models.TrackPoint[]? Result { get; set; } =
        [
            new OnaPlotter.Models.TrackPoint(
                Timestamp: DateTime.UtcNow.AddMinutes(-2),
                Latitude: 54.5, Longitude: 11.2,
                SpeedOverGround: 2.1, CourseOverGround: null, Heading: null,
                WindAngleApparent: null, WindSpeedApparent: null,
                WindAngleTrue: null, WindSpeedTrue: null),
            new OnaPlotter.Models.TrackPoint(
                Timestamp: DateTime.UtcNow.AddMinutes(-1),
                Latitude: 54.6, Longitude: 11.3,
                SpeedOverGround: 2.4, CourseOverGround: null, Heading: null,
                WindAngleApparent: null, WindSpeedApparent: null,
                WindAngleTrue: null, WindSpeedTrue: null),
        ];

        // The light position-only fetch isn't exercised by
        // ServerTrackController (it switched to the rich fetch so the
        // map polyline can be SOG-coloured like the local trail).
        // Unused but required by the interface.
        public Task<double[][]?> GetServerTrackAsync(string timespan = "1d", string resolution = "1m", CancellationToken ct = default)
            => Task.FromResult<double[][]?>(null);

        public Task<OnaPlotter.Models.TrackPoint[]?> GetServerTrackPointsAsync(
            DateTimeOffset? from, DateTimeOffset? to, string? timespan,
            string resolution = "30s", OnaPlotter.Models.TrackBbox? bbox = null, CancellationToken ct = default)
        {
            Calls.Add((timespan ?? "1d", resolution));
            return Task.FromResult(Result);
        }
    }

    private static (ServerTrackController ctrl, FakeOverlaysJs js, FakeTrackApi api, List<string> infos)
        New()
    {
        var js = new FakeOverlaysJs();
        var api = new FakeTrackApi();
        var infos = new List<string>();
        var ctrl = new ServerTrackController(js, api, msg => infos.Add(msg));
        return (ctrl, js, api, infos);
    }

    [Test]
    public async Task Toggle_On_Fetches_And_Pushes()
    {
        var (ctrl, js, api, _) = New();

        await ctrl.ToggleAsync(true);

        await Assert.That(api.Calls.Count).IsEqualTo(1);
        await Assert.That(api.Calls[0].span).IsEqualTo("1d");
        await Assert.That(js.ServerSets.Count).IsEqualTo(1);
        await Assert.That(ctrl.Visible).IsTrue();
    }

    [Test]
    public async Task Toggle_Off_Clears()
    {
        var (ctrl, js, api, _) = New();
        await ctrl.ToggleAsync(true);

        await ctrl.ToggleAsync(false);

        await Assert.That(js.Clears).IsEqualTo(1);
        await Assert.That(ctrl.Visible).IsFalse();
    }

    [Test]
    public async Task Duration_Change_Refetches_When_Visible()
    {
        var (ctrl, js, api, _) = New();
        await ctrl.ToggleAsync(true);

        await ctrl.SetDurationAsync("3d");

        await Assert.That(api.Calls.Count).IsEqualTo(2);
        await Assert.That(api.Calls[1].span).IsEqualTo("3d");
        await Assert.That(ctrl.Duration).IsEqualTo("3d");
    }

    [Test]
    public async Task Duration_Change_Skipped_When_Hidden()
    {
        var (ctrl, js, api, _) = New();

        await ctrl.SetDurationAsync("3d");

        await Assert.That(api.Calls.Count).IsEqualTo(0);
        await Assert.That(ctrl.Duration).IsEqualTo("3d");
    }

    [Test]
    public async Task Duration_Change_Same_Value_Is_No_Op()
    {
        // Idempotency: if the helm picks the duration that's already
        // active, we don't re-fetch.
        var (ctrl, js, api, _) = New();
        await ctrl.ToggleAsync(true);

        await ctrl.SetDurationAsync("1d");

        await Assert.That(api.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task All_Duration_Maps_To_100_Year_Iso_Span()
    {
        // SignalK History API has no "everything" shape; controller
        // substitutes a span larger than any plausible cruise.
        var (ctrl, js, api, _) = New();

        await ctrl.SetDurationAsync("all");
        await ctrl.ToggleAsync(true);

        await Assert.That(api.Calls[0].span).IsEqualTo("P36500D");
    }

    [Test]
    public async Task Empty_Result_Auto_Disables_And_Surfaces_Info()
    {
        // Helm enabled the layer for a span with no history; the
        // controller drops the visible flag, clears the JS layer, and
        // tells the helm so the silent failure is visible.
        var (ctrl, js, api, infos) = New();
        api.Result = [];

        await ctrl.ToggleAsync(true);

        await Assert.That(ctrl.Visible).IsFalse();
        await Assert.That(js.Clears).IsEqualTo(1);
        await Assert.That(infos.Count).IsEqualTo(1);
        await Assert.That(infos[0]).Contains("No history points");
    }

    [Test]
    public async Task Push_Carries_Sog_Triples_Not_Pairs()
    {
        // Server-track parity with the local trail requires the JS
        // layer to receive [lat, lon, sogMs] triples so it can colour
        // by speed bucket. Pin the shape so a regression back to
        // [lat, lon] pairs (and the grey single-line render) breaks
        // this test.
        var (ctrl, js, _, _) = New();

        await ctrl.ToggleAsync(true);

        await Assert.That(js.ServerSets.Count).IsEqualTo(1);
        var pushed = js.ServerSets[0].coords;
        await Assert.That(pushed.Length).IsEqualTo(2);
        await Assert.That(pushed[0].Length).IsEqualTo(3);
        await Assert.That(pushed[0][2]).IsEqualTo(2.1);
        await Assert.That(pushed[1][2]).IsEqualTo(2.4);
    }

    [Test]
    public async Task Resolution_Change_Refetches_When_Visible()
    {
        var (ctrl, _, api, _) = New();
        await ctrl.ToggleAsync(true);

        await ctrl.SetResolutionAsync("5m");

        await Assert.That(api.Calls.Count).IsEqualTo(2);
        await Assert.That(api.Calls[1].resolution).IsEqualTo("5m");
        await Assert.That(ctrl.Resolution).IsEqualTo("5m");
    }

    [Test]
    public async Task Resolution_Change_Skipped_When_Hidden()
    {
        var (ctrl, _, api, _) = New();

        await ctrl.SetResolutionAsync("5m");

        await Assert.That(api.Calls.Count).IsEqualTo(0);
        await Assert.That(ctrl.Resolution).IsEqualTo("5m");
    }

    [Test]
    public async Task Resolution_Change_Same_Value_Is_No_Op()
    {
        var (ctrl, _, api, _) = New();
        await ctrl.ToggleAsync(true);

        await ctrl.SetResolutionAsync("1m");

        await Assert.That(api.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Refresh_Refetches_When_Visible()
    {
        // The route-active 60 s refresher kicks RefreshAsync; pin
        // that it actually re-fetches with the current duration /
        // resolution rather than no-opping.
        var (ctrl, _, api, _) = New();
        await ctrl.ToggleAsync(true);

        await ctrl.RefreshAsync();

        await Assert.That(api.Calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Refresh_NoOp_When_Hidden()
    {
        // Helm has the layer off; a refresh tick from the route-active
        // refresher should not burn an HTTP round-trip on a polyline
        // that isn't being rendered.
        var (ctrl, _, api, _) = New();

        await ctrl.RefreshAsync();

        await Assert.That(api.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Within_Bounds_Toggle_Skips_Refetch()
    {
        // The JS module owns the cached coord array; clip-to-bounds
        // is a JS-side decision that doesn't need a fresh HTTP.
        var (ctrl, js, api, _) = New();
        await ctrl.ToggleAsync(true);

        await ctrl.SetWithinBoundsAsync(true);

        await Assert.That(api.Calls.Count).IsEqualTo(1);     // initial only
        await Assert.That(js.ClipFlags).IsEquivalentTo([true]);
        await Assert.That(ctrl.WithinBounds).IsTrue();
    }
}
