using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Js;
using OnaPlotter.Services.Map;
using OnaPlotter.Services.Pois;
using OnaPlotter.Services.Settings;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the controller's contracts: degenerate-bbox guard, the
/// no-categories-enabled short-circuit (push empty, don't fetch),
/// and the cache-then-fetch render order. The fetch debounce
/// (800 ms) isn't exercised here -- it's covered by integration
/// behaviour and would slow the suite without finding bugs the
/// service / cache tests don't already cover.
/// </summary>
public class MarinePoiControllerTests
{
    // Predicate signature: IsBboxRenderable(west, south, east, north, zoom).
    // West / east are longitudes; south / north are latitudes. The
    // parameterised cases below test (in order): normal viewport,
    // low-zoom-but-still-renderable, NaN, infinity, lat-out-of-range
    // (south, then north), zero-area degenerate (lat-span,
    // lon-span), and antimeridian-crossing (valid).
    [Test]
    [Arguments(-75.0, 40.0, -74.0, 41.0, 12.0, true)]
    [Arguments(-75.0, 40.0, -74.0, 41.0, 8.0, true)]
    [Arguments(double.NaN, 40.0, -74.0, 41.0, 12.0, false)]
    [Arguments(-75.0, double.NaN, -74.0, 41.0, 12.0, false)]
    [Arguments(-75.0, 40.0, double.PositiveInfinity, 41.0, 12.0, false)]
    [Arguments(-75.0, -91.0, -74.0, 41.0, 12.0, false)]
    [Arguments(-75.0, 40.0, -74.0, 91.0, 12.0, false)]
    [Arguments(-75.0, 40.0, -74.0, 40.0, 12.0, false)]
    [Arguments(-75.0, 40.0, -75.0, 41.0, 12.0, false)]
    [Arguments(175.0, -1.0, -175.0, 1.0, 12.0, true)]
    public async Task IsBboxRenderable_BoundaryCases(
        double west, double south, double east, double north, double zoom, bool expected)
    {
        await Assert.That(MarinePoiController.IsBboxRenderable(west, south, east, north, zoom))
            .IsEqualTo(expected);
    }

    [Test]
    public async Task NoCategoriesEnabled_PushesEmpty_DoesNotFetch()
    {
        // Layers panel still in defaults: helm has not opted in. The
        // controller should push an empty payload (so any leftover
        // markers from a previous category set drop), and skip the
        // Overpass round-trip entirely.
        var ctx = NewContext();
        await ctx.Controller.OnBoundsChangedAsync(-75, 40, -74, 41, 12);
        // Cache push fires once on the bounds change; an empty payload
        // is the no-categories rendering of an empty cache.
        await Assert.That(ctx.Js.PushedSets.Count).IsEqualTo(1);
        await Assert.That(ctx.Js.PushedSets[0].Length).IsEqualTo(0);
        await Assert.That(ctx.Service.FetchCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task CategoryEnabled_RendersFromCache_OnEveryBoundsChange()
    {
        // Cache pre-populated with one marina inside the test bbox.
        // Toggling Marina on + reporting bounds must push the marina
        // to JS without waiting for the Overpass debounce -- the
        // cache is the source of truth for instant paint.
        var ctx = NewContext();
        ctx.Settings.MarinePoiMarinaEnabled = true;
        await ctx.Cache.MergeAsync([
            Poi("n1", 40.5, -74.5, MarinePoiCategory.Marina),
        ]);

        await ctx.Controller.OnBoundsChangedAsync(-75, 40, -74, 41, 12);

        await Assert.That(ctx.Js.PushedSets.Count).IsEqualTo(1);
        await Assert.That(ctx.Js.PushedSets[0].Length).IsEqualTo(1);
    }

    [Test]
    public async Task DegenerateBbox_ShortCircuits()
    {
        // Pre-layout race: Leaflet hands C# a 0-area bbox during
        // page reload. The controller must not fire a useless fetch
        // or stamp its viewport state.
        var ctx = NewContext();
        ctx.Settings.MarinePoiMarinaEnabled = true;
        await ctx.Controller.OnBoundsChangedAsync(7.0, 43.5, 7.0, 43.5, 14);

        await Assert.That(ctx.Js.PushedSets.Count).IsEqualTo(0);
        await Assert.That(ctx.Service.FetchCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task SettingsChange_BeforeViewport_NoOp()
    {
        // OnSettingsChangedAsync must not fire a fetch / render when
        // the map hasn't reported its first viewport yet (fresh page
        // mount, helm flips a category before the map settles).
        var ctx = NewContext();
        ctx.Settings.MarinePoiMarinaEnabled = true;
        await ctx.Controller.OnSettingsChangedAsync();

        await Assert.That(ctx.Js.PushedSets.Count).IsEqualTo(0);
        await Assert.That(ctx.Service.FetchCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task SettingsChange_AfterViewport_PushesVisibilityFlag()
    {
        // Toggle a category after the viewport is known: the
        // controller flips the JS master gate (so the LAYER becomes
        // visible) and re-renders the cache through the new mask.
        var ctx = NewContext();
        await ctx.Controller.OnBoundsChangedAsync(-75, 40, -74, 41, 12);
        var pushesBefore = ctx.Js.PushedSets.Count;
        var visibilityCallsBefore = ctx.Js.VisibilityCalls.Count;

        ctx.Settings.MarinePoiMarinaEnabled = true;
        await ctx.Controller.OnSettingsChangedAsync();

        // Visibility gate flipped to true (any-category-enabled).
        await Assert.That(ctx.Js.VisibilityCalls.Count).IsEqualTo(visibilityCallsBefore + 1);
        await Assert.That(ctx.Js.VisibilityCalls[^1]).IsTrue();
        // Re-rendered the cache through the new mask.
        await Assert.That(ctx.Js.PushedSets.Count).IsEqualTo(pushesBefore + 1);
    }

    private static MarinePoi Poi(string id, double lat, double lon, MarinePoiCategory cat) => new(
        Id: id,
        Lat: lat,
        Lon: lon,
        Category: cat,
        Name: id,
        Tags: new Dictionary<string, string>(),
        LastSeenUtc: new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc));

    private static TestContext NewContext()
    {
        var kv = new InMemoryKv();
        var cache = new MarinePoiCache(kv, NullLogger<MarinePoiCache>.Instance);
        var service = new RecordingService();
        var js = new RecordingJs();
        var settings = new MutableSettings();
        var controller = new MarinePoiController(
            service, cache, js, settings,
            NullLogger<MarinePoiController>.Instance);
        return new TestContext(controller, cache, service, js, settings);
    }

    private sealed record TestContext(
        MarinePoiController Controller,
        MarinePoiCache Cache,
        RecordingService Service,
        RecordingJs Js,
        MutableSettings Settings);

    private sealed class RecordingJs : IMapMarinePoiJs
    {
        public List<object[]> PushedSets { get; } = [];
        public List<bool> VisibilityCalls { get; } = [];

        public Task SetMarinePoisAsync(object[] pois)
        {
            PushedSets.Add(pois);
            return Task.CompletedTask;
        }

        public Task SetMarinePoisVisibleAsync(bool visible)
        {
            VisibilityCalls.Add(visible);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingService : IMarinePoiService
    {
        public int FetchCallCount { get; private set; }
        public IReadOnlyList<MarinePoi> NextResult { get; set; } = [];

        public Task<IReadOnlyList<MarinePoi>> FetchAsync(
            double south, double west, double north, double east,
            IReadOnlySet<MarinePoiCategory> categories,
            CancellationToken ct = default)
        {
            FetchCallCount++;
            return Task.FromResult(NextResult);
        }
    }

    private sealed class MutableSettings : IMarinePoiSettings
    {
        public bool MarinePoiFuelEnabled { get; set; }
        public bool MarinePoiMarinaEnabled { get; set; }
        public bool MarinePoiHarbourEnabled { get; set; }
        public bool MarinePoiMooringEnabled { get; set; }
        public bool MarinePoiSlipwayEnabled { get; set; }
        public bool MarinePoiPierEnabled { get; set; }
        public bool MarinePoiChandleryEnabled { get; set; }
        public bool MarinePoiDrinkingWaterEnabled { get; set; }
        public bool MarinePoiPumpOutEnabled { get; set; }
        public Task SetMarinePoiFuelEnabledAsync(bool v) { MarinePoiFuelEnabled = v; return Task.CompletedTask; }
        public Task SetMarinePoiMarinaEnabledAsync(bool v) { MarinePoiMarinaEnabled = v; return Task.CompletedTask; }
        public Task SetMarinePoiHarbourEnabledAsync(bool v) { MarinePoiHarbourEnabled = v; return Task.CompletedTask; }
        public Task SetMarinePoiMooringEnabledAsync(bool v) { MarinePoiMooringEnabled = v; return Task.CompletedTask; }
        public Task SetMarinePoiSlipwayEnabledAsync(bool v) { MarinePoiSlipwayEnabled = v; return Task.CompletedTask; }
        public Task SetMarinePoiPierEnabledAsync(bool v) { MarinePoiPierEnabled = v; return Task.CompletedTask; }
        public Task SetMarinePoiChandleryEnabledAsync(bool v) { MarinePoiChandleryEnabled = v; return Task.CompletedTask; }
        public Task SetMarinePoiDrinkingWaterEnabledAsync(bool v) { MarinePoiDrinkingWaterEnabled = v; return Task.CompletedTask; }
        public Task SetMarinePoiPumpOutEnabledAsync(bool v) { MarinePoiPumpOutEnabled = v; return Task.CompletedTask; }
    }

    private sealed class InMemoryKv : IKeyValueStore
    {
        public Dictionary<string, string> Store { get; } = [];
        public Task<string?> GetAsync(string k, CancellationToken ct = default)
            => Task.FromResult(Store.TryGetValue(k, out var v) ? v : null);
        public Task SetAsync(string k, string v, CancellationToken ct = default)
        { Store[k] = v; return Task.CompletedTask; }
        public Task RemoveAsync(string k, CancellationToken ct = default)
        { Store.Remove(k); return Task.CompletedTask; }
    }
}
