using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the synthetic OSM + OpenSeaMap chart descriptors. The values
/// are persistence keys (Identifier) and licence-required strings
/// (Attribution); a refactor that drops or renames either silently
/// loses the helm's enabled-charts selection on next load OR breaks
/// the ODbL / CC-BY-SA credit requirement.
/// </summary>
public class BuiltInChartsTests
{
    [Test]
    public async Task Osm_Has_Stable_Identifier_And_InternetTileUrl()
    {
        var osm = BuiltInCharts.All.First(c => c.Identifier == BuiltInCharts.OsmIdentifier);
        await Assert.That(osm.Identifier).IsEqualTo("osm");
        await Assert.That(osm.GetTileUrl())
            .IsEqualTo("https://tile.openstreetmap.org/{z}/{x}/{y}.png")
            .Because("OSM tiles fetch directly from openstreetmap.org, NOT from the SK server");
        // Native tile cap. The map's L.map maxZoom is 19 + ChartUpscale.MaxLevels;
        // OSM stays at 19 so it goes blank past native (helm-tested
        // preference: no blurry basemap competing with the chart).
        await Assert.That(osm.MaxZoom).IsEqualTo(19);
        await Assert.That(osm.AllowUpscale)
            .IsFalse()
            .Because("OSM upscale would compete visually with the chart's GPU upscale");
        await Assert.That(osm.Opacity)
            .IsEqualTo(1.0)
            .Because("OSM is the basemap; opacity-stacking dimming would hurt readability");
    }

    [Test]
    public async Task OpenSeaMap_Has_Stable_Identifier_And_InternetTileUrl()
    {
        var sea = BuiltInCharts.All.First(c => c.Identifier == BuiltInCharts.OpenSeaMapIdentifier);
        await Assert.That(sea.Identifier).IsEqualTo("openseamap");
        await Assert.That(sea.GetTileUrl())
            .IsEqualTo("https://tiles.openseamap.org/seamark/{z}/{x}/{y}.png");
        await Assert.That(sea.MaxZoom).IsEqualTo(19);
        await Assert.That(sea.AllowUpscale).IsFalse();
        await Assert.That(sea.Opacity)
            .IsEqualTo(0.8)
            .Because("OpenSeaMap is a transparent seamark overlay; 0.8 opacity blends over the basemap");
    }

    [Test]
    public async Task BothCarryAttributionLinks()
    {
        // ODbL (OSM) and CC-BY-SA (OpenSeaMap) both require visible
        // credit. The strings here flow through addChartLayer's
        // attribution arg into Leaflet's bottom-right control.
        var osm = BuiltInCharts.All.First(c => c.Identifier == BuiltInCharts.OsmIdentifier);
        var sea = BuiltInCharts.All.First(c => c.Identifier == BuiltInCharts.OpenSeaMapIdentifier);

        await Assert.That(osm.Attribution).Contains("openstreetmap.org/copyright");
        await Assert.That(osm.Attribution).Contains("&copy; OpenStreetMap");
        await Assert.That(sea.Attribution).Contains("openseamap.org");
        await Assert.That(sea.Attribution).Contains("&copy; OpenSeaMap");
    }

    [Test]
    public async Task IsBuiltIn_RecognisesBothAndRejectsOthers()
    {
        await Assert.That(BuiltInCharts.IsBuiltIn(BuiltInCharts.OsmIdentifier)).IsTrue();
        await Assert.That(BuiltInCharts.IsBuiltIn(BuiltInCharts.OpenSeaMapIdentifier)).IsTrue();
        await Assert.That(BuiltInCharts.IsBuiltIn("noaa-rnc")).IsFalse();
        await Assert.That(BuiltInCharts.IsBuiltIn("")).IsFalse();
        await Assert.That(BuiltInCharts.IsBuiltIn("OSM")).IsFalse()
            .Because("identifier compare is case-sensitive (matches StringComparer.Ordinal in EnabledChartIds)");
    }

    [Test]
    public async Task All_IsImmutable_ReadOnlyList()
    {
        // Tests + production code can iterate but must not mutate the
        // shared list. Pin via the IReadOnlyList contract; a refactor
        // that switched to List<SignalkChart> would surface here.
        await Assert.That(BuiltInCharts.All).IsTypeOf<IReadOnlyList<OnaPlotter.Models.SignalkChart>>();
        await Assert.That(BuiltInCharts.All.Count).IsEqualTo(2);
    }
}
