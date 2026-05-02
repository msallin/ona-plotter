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

        /// <summary>Records min / max zoom and bounds. Lets tests pin
        /// the controller's positive-int guard for hostile MaxZoom
        /// values and bounds forwarding.</summary>
        public List<int> MinZoomCalls { get; } = [];
        public List<int> MaxZoomCalls { get; } = [];
        public List<double[]?> BoundsCalls { get; } = [];

        public Task AddChartLayerAsync(string id, string tileUrl, int minZoom, int maxZoom, double opacity, double[]? bounds, int upscaleLevels, string attribution)
        {
            if (NextAddError is not null) { var e = NextAddError; NextAddError = null; throw e; }
            Added.Add(id);
            UpscaleLevelsCalls.Add(upscaleLevels);
            AttributionCalls.Add(attribution);
            OpacityCalls.Add(opacity);
            MinZoomCalls.Add(minZoom);
            MaxZoomCalls.Add(maxZoom);
            BoundsCalls.Add(bounds);
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
        public bool GuardZoneWarningRingVisible => true;
        public double WeatherOverlayOpacity => 0.5;
        public bool HarborMode => false;
        public bool BigType => false;
        public bool ExpandAllHud => false;
        public bool ShowAutopilotHud => false;
        public bool ShowRadarHud => false;
        public bool ShowDefaultHud => true;
        public double OwnCogVectorMinutes => 10.0;
        public double AisCogVectorMinutes => 10.0;
        public Task SetMapOrientationAsync(string v) => Task.CompletedTask;
        public Task SetFollowBoatAsync(bool v) => Task.CompletedTask;
        public Task SetLaylinesVisibleAsync(bool v) => Task.CompletedTask;
        public Task SetAtonsVisibleAsync(bool v) => Task.CompletedTask;
        public Task SetGuardZoneVisibleAsync(bool v) => Task.CompletedTask;
        public Task SetGuardZoneWarningRingVisibleAsync(bool v) => Task.CompletedTask;
        public Task SetWeatherOverlayOpacityAsync(double v) => Task.CompletedTask;
        public Task SetChartUpscaleEnabledAsync(bool v) { ChartUpscaleEnabled = v; return Task.CompletedTask; }
        public Task SetChartUpscaleLevelsAsync(int v) { ChartUpscaleLevels = v; return Task.CompletedTask; }
        public Task SetHarborModeAsync(bool v) => Task.CompletedTask;
        public Task SetBigTypeAsync(bool v) => Task.CompletedTask;
        public Task SetExpandAllHudAsync(bool v) => Task.CompletedTask;
        public Task SetShowAutopilotHudAsync(bool v) => Task.CompletedTask;
        public Task SetShowRadarHudAsync(bool v) => Task.CompletedTask;
        public Task SetShowDefaultHudAsync(bool v) => Task.CompletedTask;
        public Task SetOwnCogVectorMinutesAsync(double v) => Task.CompletedTask;
        public Task SetAisCogVectorMinutesAsync(double v) => Task.CompletedTask;
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
    public async Task Toggle_On_PassesOpacityFromChart()
    {
        var (ctrl, js, _, _, _) = New();
        var chart = Chart("c1");
        chart.Opacity = 1.0;

        await ctrl.ToggleAsync(chart, true);

        await Assert.That(js.OpacityCalls[0]).IsEqualTo(1.0);
    }

    [Test]
    public async Task Toggle_On_TrustedAttribution_PassesThroughRaw()
    {
        // Built-in OSM / OpenSeaMap entries ship IsTrustedAttribution
        // = true so their hardcoded ODbL credits keep their clickable
        // links. The sanitizer must not strip them.
        var (ctrl, js, _, _, _) = New();
        var chart = Chart("c1");
        chart.Attribution = "<a href=\"https://x\">© X</a>";
        chart.IsTrustedAttribution = true;

        await ctrl.ToggleAsync(chart, true);

        await Assert.That(js.AttributionCalls[0]).IsEqualTo(chart.Attribution);
    }

    [Test]
    public async Task Toggle_On_UntrustedAttribution_HtmlEscaped()
    {
        // SK chart-server entries default IsTrustedAttribution = false
        // because Leaflet's AttributionControl uses innerHTML; a
        // hostile provider's "<img onerror=...>" would otherwise
        // execute in the WASM origin. Pin the escape so the tag
        // renders as visible text.
        var (ctrl, js, _, _, _) = New();
        var chart = Chart("c1");
        chart.Attribution = "<img src=x onerror=alert(1)>";
        chart.IsTrustedAttribution = false;

        await ctrl.ToggleAsync(chart, true);

        await Assert.That(js.AttributionCalls[0]).DoesNotContain("<img");
        await Assert.That(js.AttributionCalls[0]).Contains("&lt;img");
    }

    [Test]
    [Arguments(null, 1)]    // null -> default 1
    [Arguments(0, 1)]       // zero -> default
    [Arguments(-5, 1)]      // negative -> default (the load-bearing case)
    [Arguments(2, 2)]       // positive -> pass through
    public async Task Toggle_On_MinZoom_NegativeOrNull_ClampedToOne(int? input, int expected)
    {
        // A hostile or buggy chart provider sending negative MinZoom
        // would otherwise reach the JS layer where the `|| 1` falsy
        // idiom doesn't catch negatives -- the layer's calibrator
        // floor (`native <= minZ + 1`) would misfire.
        var (ctrl, js, _, _, _) = New();
        var chart = Chart("c1");
        chart.MinZoom = input;

        await ctrl.ToggleAsync(chart, true);

        await Assert.That(js.MinZoomCalls[0]).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null, 18)]   // null -> default 18
    [Arguments(0, 18)]      // zero -> default
    [Arguments(-1, 18)]     // negative -> default (the load-bearing case)
    [Arguments(15, 15)]     // positive -> pass through
    [Arguments(22, 22)]     // upper-edge -> pass through (Leaflet caps via map maxZoom anyway)
    public async Task Toggle_On_MaxZoom_NegativeOrNull_ClampedToEighteen(int? input, int expected)
    {
        var (ctrl, js, _, _, _) = New();
        var chart = Chart("c1");
        chart.MaxZoom = input;

        await ctrl.ToggleAsync(chart, true);

        await Assert.That(js.MaxZoomCalls[0]).IsEqualTo(expected);
    }

    [Test]
    [Arguments(false, 0, true, 0)]    // master off -> 0
    [Arguments(false, 3, true, 0)]    // master off, levels irrelevant
    [Arguments(false, 3, false, 0)]   // both off
    [Arguments(true, 0, true, 0)]     // master on, levels=0 -> 0 (A/B path)
    [Arguments(true, 2, true, 2)]     // happy path
    [Arguments(true, 3, true, 3)]     // max levels
    [Arguments(true, 3, false, 0)]    // chart opt-out forces 0 even when master on
    [Arguments(true, 99, true, 3)]    // out-of-range clamps via Effective
    [Arguments(true, -5, true, 0)]    // out-of-range below floor clamps via Effective
    public async Task Toggle_On_UpscaleResolution_CrossProduct(
        bool masterEnabled, int configuredLevels, bool chartAllowUpscale, int expectedJsValue)
    {
        // The 6-cell truth table: (master flag, levels, chart opt-out)
        // -> what lands in the JS upscaleLevels arg. Two cells were
        // pinned by previous tests; this matrix locks down all six
        // plus the out-of-range edges so a regression in either
        // ChartUpscale.Effective or the controller's AllowUpscale
        // gating is one assertion away from a red.
        var (ctrl, js, _, display, _) = New();
        display.ChartUpscaleEnabled = masterEnabled;
        display.ChartUpscaleLevels = configuredLevels;
        var chart = Chart("c1");
        chart.AllowUpscale = chartAllowUpscale;

        await ctrl.ToggleAsync(chart, true);

        await Assert.That(js.UpscaleLevelsCalls.Count).IsEqualTo(1);
        await Assert.That(js.UpscaleLevelsCalls[0]).IsEqualTo(expectedJsValue);
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
