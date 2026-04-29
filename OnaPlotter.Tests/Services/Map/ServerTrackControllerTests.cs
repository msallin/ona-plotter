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

        public Task AddChartLayerAsync(string id, string tileUrl, int minZoom, int maxZoom, double opacity, double[]? bounds) => Task.CompletedTask;
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
        public double[][]? Result { get; set; } = [[54.5, 11.2], [54.6, 11.3]];

        public Task<double[][]?> GetServerTrackAsync(string timespan = "1d", string resolution = "1m", CancellationToken ct = default)
        {
            Calls.Add((timespan, resolution));
            return Task.FromResult(Result);
        }

        // The rich fetch isn't exercised by ServerTrackController (the
        // Map page only needs the lightweight position-only track).
        // Unused but required by the interface; null-result mirrors
        // "no data" in production.
        public Task<OnaPlotter.Models.TrackPoint[]?> GetServerTrackPointsAsync(
            DateTimeOffset? from, DateTimeOffset? to, string? timespan,
            string resolution = "30s", OnaPlotter.Models.TrackBbox? bbox = null, CancellationToken ct = default)
            => Task.FromResult<OnaPlotter.Models.TrackPoint[]?>(null);
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
