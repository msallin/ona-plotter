using OnaPlotter.Models;
using OnaPlotter.Services.Js;
using OnaPlotter.Services.Map;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the chart enable / quick-bar / reorder lifecycle. The
/// reorder + ApplyOrder bits are the highest-value tests: a stale
/// chart-order list silently broke the up/down arrows in the field
/// before the safety-pad fix this controller still carries.
/// </summary>
public class ChartLayerControllerTests
{
    private sealed class FakeOverlaysJs : IMapOverlaysJs
    {
        public List<string> Added { get; } = [];
        public List<string> Removed { get; } = [];
        public List<string[]> OrderPushes { get; } = [];

        public Microsoft.JSInterop.JSException? NextAddError { get; set; }
        public Microsoft.JSInterop.JSException? NextRemoveError { get; set; }
        public Microsoft.JSInterop.JSException? NextOrderError { get; set; }

        /// <summary>Records the most recent upscaleLevels arg so tests
        /// can assert the controller resolved it correctly from
        /// IMapDisplaySettings.ChartUpscaleEnabled / Levels.</summary>
        public List<int> UpscaleLevelsCalls { get; } = [];

        /// <summary>Records the most recent attribution arg. Built-in
        /// OSM / OpenSeaMap charts ship a non-empty ODbL credit; SK
        /// chart-server entries ship empty.</summary>
        public List<string> AttributionCalls { get; } = [];

        /// <summary>Records the most recent opacity arg. Built-in OSM
        /// is 1.0 (basemap); OpenSeaMap is 0.8 (transparent overlay);
        /// SK charts default 0.8.</summary>
        public List<double> OpacityCalls { get; } = [];

        public Task AddChartLayerAsync(string id, string tileUrl, int minZoom, int maxZoom, double opacity, double[]? bounds, int upscaleLevels, string attribution)
        {
            if (NextAddError is not null) { var e = NextAddError; NextAddError = null; throw e; }
            Added.Add(id);
            UpscaleLevelsCalls.Add(upscaleLevels);
            AttributionCalls.Add(attribution);
            OpacityCalls.Add(opacity);
            return Task.CompletedTask;
        }

        public Task RemoveChartLayerAsync(string id)
        {
            if (NextRemoveError is not null) { var e = NextRemoveError; NextRemoveError = null; throw e; }
            Removed.Add(id);
            return Task.CompletedTask;
        }

        public Task SetChartLayerOrderAsync(string[] orderedIds)
        {
            if (NextOrderError is not null) { var e = NextOrderError; NextOrderError = null; throw e; }
            OrderPushes.Add(orderedIds);
            return Task.CompletedTask;
        }

        public Task SetWeatherOverlayAsync(string tileUrl, double opacity) => Task.CompletedTask;
        public Task SetWeatherOverlayOpacityAsync(double opacity) => Task.CompletedTask;
        public Task ClearWeatherOverlayAsync() => Task.CompletedTask;
        public Task SetColoredTrackAsync(double[][] points) => Task.CompletedTask;
        public Task SetServerTrackAsync(double[][] coords, bool clipToBounds) => Task.CompletedTask;
        public Task SetServerTrackClipToBoundsAsync(bool enabled) => Task.CompletedTask;
        public Task ClearServerTrackAsync() => Task.CompletedTask;
    }

    private static SignalkChart Chart(string id, string name = "Chart") => new()
    {
        Identifier = id,
        Name = name,
        TilemapUrl = $"https://tile.example/{id}/{{z}}/{{x}}/{{y}}.png",
        MinZoom = 1,
        MaxZoom = 18,
    };

    /// <summary>Minimal IChartSettings implementation that round-trips
    /// through real persisted state. Lets the controller's reorder /
    /// quick-bar / enabled-set logic drive against a live store.</summary>
    private sealed class ChartFakeSettings : OnaPlotter.Services.Settings.IChartSettings
    {
        private HashSet<string> _enabled = new(StringComparer.Ordinal);
        private HashSet<string> _quickBar = new(StringComparer.Ordinal);
        private List<string> _order = [];

        public bool ChartsSeeded => false;
        public IReadOnlySet<string> EnabledChartIds => _enabled;
        public IReadOnlySet<string> EnabledRouteIds => new HashSet<string>();
        public IReadOnlySet<string> QuickBarChartIds => _quickBar;
        public IReadOnlyList<string> ChartOrder => _order;

        public Task MarkChartsSeededAsync() => Task.CompletedTask;

        public Task SetEnabledChartsAsync(IEnumerable<string> ids)
        {
            _enabled = new HashSet<string>(ids, StringComparer.Ordinal);
            return Task.CompletedTask;
        }

        public Task SetEnabledRoutesAsync(IEnumerable<string> ids) => Task.CompletedTask;

        public Task SetQuickBarChartsAsync(IEnumerable<string> ids)
        {
            _quickBar = new HashSet<string>(ids, StringComparer.Ordinal);
            return Task.CompletedTask;
        }

        public Task SetChartOrderAsync(IEnumerable<string> ids)
        {
            _order = ids.ToList();
            return Task.CompletedTask;
        }
    }

    /// <summary>Minimal IMapDisplaySettings stub. The controller only
    /// consults ChartUpscaleEnabled / ChartUpscaleLevels; everything
    /// else returns sensible defaults.</summary>
    private sealed class DisplayFakeSettings : OnaPlotter.Services.Settings.IMapDisplaySettings
    {
        public bool ChartUpscaleEnabled { get; set; }
        public int ChartUpscaleLevels { get; set; } = 2;
        public string MapOrientation => "north";
        public bool FollowBoat => true;
        public bool LaylinesVisible => false;
        public bool AtonsVisible => true;
        public bool GuardZoneVisible => true;
        public double WeatherOverlayOpacity => 0.5;
        public bool HarborMode => false;
        public bool BigType => false;
        public bool ExpandAllHud => false;
        public bool ShowAutopilotHud => false;
        public bool ShowRadarHud => false;
        public bool ShowDefaultHud => true;
        public Task SetMapOrientationAsync(string v) => Task.CompletedTask;
        public Task SetFollowBoatAsync(bool v) => Task.CompletedTask;
        public Task SetLaylinesVisibleAsync(bool v) => Task.CompletedTask;
        public Task SetAtonsVisibleAsync(bool v) => Task.CompletedTask;
        public Task SetGuardZoneVisibleAsync(bool v) => Task.CompletedTask;
        public Task SetWeatherOverlayOpacityAsync(double v) => Task.CompletedTask;
        public Task SetChartUpscaleEnabledAsync(bool v) { ChartUpscaleEnabled = v; return Task.CompletedTask; }
        public Task SetChartUpscaleLevelsAsync(int v) { ChartUpscaleLevels = v; return Task.CompletedTask; }
        public Task SetHarborModeAsync(bool v) => Task.CompletedTask;
        public Task SetBigTypeAsync(bool v) => Task.CompletedTask;
        public Task SetExpandAllHudAsync(bool v) => Task.CompletedTask;
        public Task SetShowAutopilotHudAsync(bool v) => Task.CompletedTask;
        public Task SetShowRadarHudAsync(bool v) => Task.CompletedTask;
        public Task SetShowDefaultHudAsync(bool v) => Task.CompletedTask;
    }

    private static (ChartLayerController ctrl, FakeOverlaysJs js, ChartFakeSettings settings, DisplayFakeSettings display, List<(string name, string msg)> errors)
        New()
    {
        var js = new FakeOverlaysJs();
        var settings = new ChartFakeSettings();
        var display = new DisplayFakeSettings();
        var errors = new List<(string, string)>();
        var ctrl = new ChartLayerController(js, settings, display, (name, msg) => errors.Add((name, msg)));
        return (ctrl, js, settings, display, errors);
    }

    [Test]
    public async Task Toggle_On_Adds_Layer_And_Persists_Enabled()
    {
        var (ctrl, js, settings, _, _) = New();

        await ctrl.ToggleAsync(Chart("c1"), true);

        await Assert.That(js.Added).IsEquivalentTo(["c1"]);
        await Assert.That(ctrl.EnabledCharts.Contains("c1")).IsTrue();
        await Assert.That(settings.EnabledChartIds.Contains("c1")).IsTrue();
    }

    [Test]
    public async Task Toggle_On_Adds_To_Quick_Bar_And_Order()
    {
        // Auto-enable side-effects: the chart must end up in both the
        // quick-bar set and the persisted draw order list. Without the
        // first the helm has no chip to hide it again; without the
        // second the up/down reorder buttons are dead.
        var (ctrl, js, settings, _, _) = New();

        await ctrl.ToggleAsync(Chart("c1"), true);

        await Assert.That(ctrl.QuickBarCharts.Contains("c1")).IsTrue();
        await Assert.That(settings.ChartOrder).Contains("c1");
    }

    [Test]
    public async Task Toggle_Off_Removes_Layer_And_Persists()
    {
        var (ctrl, js, settings, _, _) = New();
        await ctrl.ToggleAsync(Chart("c1"), true);

        await ctrl.ToggleAsync(Chart("c1"), false);

        await Assert.That(js.Removed).IsEquivalentTo(["c1"]);
        await Assert.That(ctrl.EnabledCharts.Contains("c1")).IsFalse();
        await Assert.That(settings.EnabledChartIds.Contains("c1")).IsFalse();
    }

    [Test]
    public async Task Toggle_On_AllowUpscale_True_HonoursDisplaySettings()
    {
        // SignalkChart.AllowUpscale = true (the SK-server default)
        // means the controller resolves upscale levels via the
        // ChartUpscale.Effective(enabled, levels) path. With master
        // flag on + 3 levels, we expect 3 to land in JS.
        var (ctrl, js, _, display, _) = New();
        display.ChartUpscaleEnabled = true;
        display.ChartUpscaleLevels = 3;
        var chart = Chart("c1");
        chart.AllowUpscale = true;

        await ctrl.ToggleAsync(chart, true);

        await Assert.That(js.UpscaleLevelsCalls.Count).IsEqualTo(1);
        await Assert.That(js.UpscaleLevelsCalls[0]).IsEqualTo(3);
    }

    [Test]
    public async Task Toggle_On_AllowUpscale_False_ForcesZeroEvenWhenSettingsEnabled()
    {
        // The built-in OSM + OpenSeaMap charts ship AllowUpscale = false
        // because their upscaled tiles arriving a frame after a SK
        // chart's GPU upscale read as a flicker. Pin: the controller
        // must force levels = 0 regardless of the helm's master flag.
        // A regression that ignored AllowUpscale would re-introduce
        // the "OSM is loading on top of my chart" symptom.
        var (ctrl, js, _, display, _) = New();
        display.ChartUpscaleEnabled = true;
        display.ChartUpscaleLevels = 3;
        var chart = Chart("c1");
        chart.AllowUpscale = false;

        await ctrl.ToggleAsync(chart, true);

        await Assert.That(js.UpscaleLevelsCalls.Count).IsEqualTo(1);
        await Assert.That(js.UpscaleLevelsCalls[0])
            .IsEqualTo(0)
            .Because("AllowUpscale=false forces levels=0 regardless of Settings");
    }

    [Test]
    public async Task Toggle_On_PassesAttributionAndOpacityFromChart()
    {
        // The chart descriptor's Attribution + Opacity flow through
        // unchanged so Leaflet's bottom-right control surfaces the
        // ODbL credit and the opacity stack matches the helm's mental
        // model ("OSM is the basemap at full opacity, SK charts layer
        // at 0.8 over it").
        var (ctrl, js, _, _, _) = New();
        var chart = Chart("c1");
        chart.Attribution = "<a href=\"https://x\">© X</a>";
        chart.Opacity = 1.0;

        await ctrl.ToggleAsync(chart, true);

        await Assert.That(js.AttributionCalls[0]).IsEqualTo(chart.Attribution);
        await Assert.That(js.OpacityCalls[0]).IsEqualTo(1.0);
    }

    [Test]
    public async Task Toggle_On_Js_Failure_Surfaces_Toast_And_Skips_State()
    {
        // A JS regression must not corrupt the persisted state -- the
        // chart isn't actually on the map, so the enabled mirror stays
        // empty.
        var (ctrl, js, settings, _, errors) = New();
        js.NextAddError = new Microsoft.JSInterop.JSException("addLayer is undefined");

        await ctrl.ToggleAsync(Chart("c1", "OpenSeaMap"), true);

        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].name).IsEqualTo("OpenSeaMap");
        await Assert.That(ctrl.EnabledCharts.Contains("c1")).IsFalse();
    }

    [Test]
    public async Task Quick_Bar_Untick_Tears_Down_Active_Chart()
    {
        // Helm un-ticks the chart in Layers panel -> rendering ALSO
        // goes off (so a removed chart doesn't ghost on the map).
        var (ctrl, js, _, _, _) = New();
        await ctrl.ToggleAsync(Chart("c1"), true);

        await ctrl.ToggleQuickBarAsync(Chart("c1"), false);

        await Assert.That(js.Removed).IsEquivalentTo(["c1"]);
        await Assert.That(ctrl.QuickBarCharts.Contains("c1")).IsFalse();
        await Assert.That(ctrl.EnabledCharts.Contains("c1")).IsFalse();
    }

    [Test]
    public async Task Quick_Bar_Tick_Activates_Inactive_Chart()
    {
        // Inverse: ticking the row in the panel = "I want this
        // chart". Activates rendering so the user doesn't have to
        // tap a separate quick-bar chip after.
        var (ctrl, js, _, _, _) = New();

        await ctrl.ToggleQuickBarAsync(Chart("c1"), true);

        await Assert.That(js.Added).IsEquivalentTo(["c1"]);
        await Assert.That(ctrl.QuickBarCharts.Contains("c1")).IsTrue();
    }

    [Test]
    public async Task Reorder_Up_Swaps_With_Previous()
    {
        // Three charts in order [a, b, c]; reorder b up (-1) yields
        // [b, a, c]. Pushed to JS so the z-stack matches.
        var (ctrl, js, settings, _, _) = New();
        await ctrl.ToggleAsync(Chart("a"), true);
        await ctrl.ToggleAsync(Chart("b"), true);
        await ctrl.ToggleAsync(Chart("c"), true);

        await ctrl.ReorderAsync(Chart("b"), -1);

        await Assert.That(settings.ChartOrder).IsEquivalentTo(["b", "a", "c"]);
        await Assert.That(js.OrderPushes[^1]).IsEquivalentTo(new[] { "b", "a", "c" });
    }

    [Test]
    public async Task Reorder_Down_Swaps_With_Next()
    {
        var (ctrl, js, settings, _, _) = New();
        await ctrl.ToggleAsync(Chart("a"), true);
        await ctrl.ToggleAsync(Chart("b"), true);
        await ctrl.ToggleAsync(Chart("c"), true);

        await ctrl.ReorderAsync(Chart("a"), +1);

        await Assert.That(settings.ChartOrder).IsEquivalentTo(["b", "a", "c"]);
    }

    [Test]
    public async Task Reorder_At_Top_Edge_Is_No_Op()
    {
        var (ctrl, js, settings, _, _) = New();
        await ctrl.ToggleAsync(Chart("a"), true);
        await ctrl.ToggleAsync(Chart("b"), true);

        int pushesBefore = js.OrderPushes.Count;
        await ctrl.ReorderAsync(Chart("a"), -1);

        await Assert.That(js.OrderPushes.Count).IsEqualTo(pushesBefore);
        await Assert.That(settings.ChartOrder).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task Reorder_Pads_Order_With_Missing_Enabled_Charts()
    {
        // Defensive: a stale ChartOrder list missing some currently-
        // enabled ids must NOT silently break the reorder. The
        // controller pads the list before the swap.
        var (ctrl, js, settings, _, _) = New();
        await ctrl.ToggleAsync(Chart("a"), true);
        await ctrl.ToggleAsync(Chart("b"), true);
        // Simulate a stale state: persisted order has only "a".
        await settings.SetChartOrderAsync(new[] { "a" });

        await ctrl.ReorderAsync(Chart("b"), -1);

        await Assert.That(settings.ChartOrder).Contains("b");
        await Assert.That(settings.ChartOrder.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Apply_Order_Sorts_Charts_By_Persisted_Index()
    {
        var (ctrl, _, settings, _, _) = New();
        await settings.SetChartOrderAsync(new[] { "c", "a", "b" });
        var charts = new[] { Chart("a", "Alpha"), Chart("b", "Bravo"), Chart("c", "Charlie") };

        var sorted = ctrl.ApplyOrder(charts);

        await Assert.That(sorted.Select(c => c.Identifier).ToList())
            .IsEquivalentTo(new[] { "c", "a", "b" });
    }

    [Test]
    public async Task Apply_Order_Appends_Unseen_Charts()
    {
        // A chart that's not in the persisted order falls to the end
        // (preserves server-provided order among the unseen).
        var (ctrl, _, settings, _, _) = New();
        await settings.SetChartOrderAsync(new[] { "a" });
        var charts = new[] { Chart("a"), Chart("b"), Chart("c") };

        var sorted = ctrl.ApplyOrder(charts);

        await Assert.That(sorted.Select(c => c.Identifier).ToList())
            .IsEquivalentTo(new[] { "a", "b", "c" });
    }

    [Test]
    public async Task Reorder_Fires_OnChartOrderChanged()
    {
        // The page wires this hook to re-sort availableCharts; the
        // reorder button looks dead without it.
        var (ctrl, _, _, _, _) = New();
        await ctrl.ToggleAsync(Chart("a"), true);
        await ctrl.ToggleAsync(Chart("b"), true);
        bool fired = false;
        ctrl.OnChartOrderChanged = () => fired = true;

        await ctrl.ReorderAsync(Chart("b"), -1);

        await Assert.That(fired).IsTrue();
    }
}
