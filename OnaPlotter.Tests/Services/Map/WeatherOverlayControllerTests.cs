using OnaPlotter.Services.Js;
using OnaPlotter.Services.Map;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the on/off + preview/commit split for the weather overlay.
/// The slider preview path explicitly does NOT persist; the commit
/// path does. Reversing those would either thrash localStorage or
/// silently drop the helm's final pick.
/// </summary>
public class WeatherOverlayControllerTests
{
    private sealed class FakeOverlaysJs : IMapOverlaysJs
    {
        public List<(string url, double opacity)> WeatherSets { get; } = [];
        public List<double> OpacityUpdates { get; } = [];
        public int WeatherClears { get; private set; }

        public Task AddChartLayerAsync(string id, string tileUrl, int minZoom, int maxZoom, double opacity, double[]? bounds, int upscaleLevels, string attribution) => Task.CompletedTask;
        public Task RemoveChartLayerAsync(string id) => Task.CompletedTask;
        public Task SetChartLayerOrderAsync(string[] orderedIds) => Task.CompletedTask;

        public Task SetWeatherOverlayAsync(string tileUrl, double opacity)
        {
            WeatherSets.Add((tileUrl, opacity));
            return Task.CompletedTask;
        }

        public Task SetWeatherOverlayOpacityAsync(double opacity)
        {
            OpacityUpdates.Add(opacity);
            return Task.CompletedTask;
        }

        public Task ClearWeatherOverlayAsync()
        {
            WeatherClears++;
            return Task.CompletedTask;
        }

        public Task SetColoredTrackAsync(double[][] points) => Task.CompletedTask;
        public Task SetServerTrackAsync(double[][] coords, bool clipToBounds) => Task.CompletedTask;
        public Task SetServerTrackClipToBoundsAsync(bool enabled) => Task.CompletedTask;
        public Task ClearServerTrackAsync() => Task.CompletedTask;
        public Task SetChartFilterAsync(string cssFilter) => Task.CompletedTask;
    }

    [Test]
    public async Task Toggle_On_Pushes_Tile_Url_With_Persisted_Opacity()
    {
        var js = new FakeOverlaysJs();
        var settings = new FakeSettings { WeatherOverlayOpacity = 0.6 };
        var ctrl = new WeatherOverlayController(js, settings);

        await ctrl.ToggleAsync(true);

        await Assert.That(js.WeatherSets.Count).IsEqualTo(1);
        await Assert.That(js.WeatherSets[0].opacity).IsEqualTo(0.6);
        await Assert.That(js.WeatherSets[0].url).Contains("rainviewer");
        await Assert.That(ctrl.Visible).IsTrue();
    }

    [Test]
    public async Task Toggle_Off_Clears_Layer()
    {
        var js = new FakeOverlaysJs();
        var ctrl = new WeatherOverlayController(js, new FakeSettings());
        await ctrl.ToggleAsync(true);

        await ctrl.ToggleAsync(false);

        await Assert.That(js.WeatherClears).IsEqualTo(1);
        await Assert.That(ctrl.Visible).IsFalse();
    }

    [Test]
    public async Task Preview_When_Hidden_Is_A_No_Op()
    {
        // Slider preview should never push when the overlay is off -
        // there's no Leaflet layer to update.
        var js = new FakeOverlaysJs();
        var ctrl = new WeatherOverlayController(js, new FakeSettings());

        await ctrl.PreviewOpacityAsync(75);

        await Assert.That(js.OpacityUpdates).IsEmpty();
    }

    [Test]
    public async Task Preview_When_Visible_Pushes_Without_Persisting()
    {
        // Slider drag tick: live update only. Persistence happens on
        // CommitOpacityAsync; the FakeSettings test double records
        // SetWeatherOverlayOpacityAsync calls via the property setter.
        var js = new FakeOverlaysJs();
        var settings = new FakeSettings { WeatherOverlayOpacity = 0.5 };
        var ctrl = new WeatherOverlayController(js, settings);
        await ctrl.ToggleAsync(true);

        await ctrl.PreviewOpacityAsync(75);

        await Assert.That(js.OpacityUpdates).IsEquivalentTo([0.75]);
        // Persisted value still the original 0.5.
        await Assert.That(settings.WeatherOverlayOpacity).IsEqualTo(0.5);
    }

    [Test]
    public async Task Commit_Persists_Final_Value()
    {
        var js = new FakeOverlaysJs();
        var settings = new FakeSettings { WeatherOverlayOpacity = 0.5 };
        var ctrl = new WeatherOverlayController(js, settings);

        await ctrl.CommitOpacityAsync(80);

        await Assert.That(settings.WeatherOverlayOpacity).IsEqualTo(0.8);
    }
}
