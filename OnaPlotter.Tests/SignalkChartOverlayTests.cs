using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins <see cref="SignalkChart.IsOverlay"/>: seamark overlays draw above
/// the weather radar, base charts below it. A misclassified base chart
/// would be drawn over the radar (hiding it); a misclassified overlay
/// would let rain hide buoys and lights.
/// </summary>
public class SignalkChartOverlayTests
{
    [Test]
    public async Task BuiltIn_OpenSeaMap_IsOverlay_Osm_IsNot()
    {
        var osm = BuiltInCharts.All.First(c => c.Identifier == BuiltInCharts.OsmIdentifier);
        var seamap = BuiltInCharts.All.First(c => c.Identifier == BuiltInCharts.OpenSeaMapIdentifier);

        await Assert.That(seamap.IsOverlay).IsTrue();
        await Assert.That(osm.IsOverlay).IsFalse();
    }

    [Test]
    [Arguments("signalk-charts:seamarks", null, true)]
    [Arguments("SeaMark-Layer", null, true)]
    [Arguments("my-overlay", "https://tiles.openseamap.org/seamark/{z}/{x}/{y}.png", true)]
    [Arguments("osm-proxy", "/signalk/chart-tiles/SEAMARK/{z}/{x}/{y}", true)]
    [Arguments("noaa-enc", "/signalk/chart-tiles/noaa/{z}/{x}/{y}", false)]
    [Arguments("", null, false)]
    public async Task SignalkServedChart_ClassifiedFromIdAndUrl(string id, string? url, bool expected)
    {
        // Real-world shapes: a chart-server plugin re-serving OpenSeaMap
        // under its own id, and a NOAA ENC base chart.
        var chart = new SignalkChart { Identifier = id, Url = url };

        await Assert.That(chart.IsOverlay).IsEqualTo(expected);
    }

    [Test]
    public async Task V1TilemapUrl_TakesPrecedenceForClassification()
    {
        var chart = new SignalkChart
        {
            Identifier = "x",
            TilemapUrl = "/signalk/v1/tiles/seamark/{z}/{x}/{y}",
            Url = "/signalk/v2/tiles/base/{z}/{x}/{y}",
        };

        await Assert.That(chart.IsOverlay).IsTrue();
    }
}
