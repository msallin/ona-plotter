using OnaPlotter.Models;
using OnaPlotter.Services.Pois;

namespace OnaPlotter.Tests.Services.Pois;

/// <summary>
/// Pin tag -> category classification + element shape handling.
/// Covers: each category's tag combinations, priority order on
/// multi-tag elements, node vs way/relation coordinate extraction,
/// out-of-range / NaN coord rejection, missing tag dict.
/// </summary>
public class OverpassResponseParserTests
{
    private static readonly DateTime Now = new(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc);

    private static OverpassElement Node(long id, double lat, double lon,
        params (string k, string v)[] tags)
    {
        var t = new Dictionary<string, string>();
        foreach (var (k, v) in tags) t[k] = v;
        return new OverpassElement
        {
            Type = "node",
            Id = id,
            Lat = lat,
            Lon = lon,
            Tags = t,
        };
    }

    private static OverpassElement Way(long id, double lat, double lon,
        params (string k, string v)[] tags)
    {
        var t = new Dictionary<string, string>();
        foreach (var (k, v) in tags) t[k] = v;
        return new OverpassElement
        {
            Type = "way",
            Id = id,
            Center = new OverpassCenter { Lat = lat, Lon = lon },
            Tags = t,
        };
    }

    [Test]
    public async Task FuelTaggedNode_ClassifiesAsFuel()
    {
        var poi = OverpassResponseParser.TryMapElement(
            Node(1, 40, -74, ("amenity", "fuel"), ("boat", "yes")), Now);
        await Assert.That(poi).IsNotNull();
        await Assert.That(poi!.Category).IsEqualTo(MarinePoiCategory.Fuel);
        await Assert.That(poi.Id).IsEqualTo("n1");
    }

    [Test]
    public async Task FuelTaggedWithoutBoatYes_DropsToUnclassified()
    {
        // A road petrol station should never make it through. The
        // query already filters but the parser's own re-check is
        // defence-in-depth - a future tag-mapping change must not
        // accidentally surface road fuel.
        var poi = OverpassResponseParser.TryMapElement(
            Node(1, 40, -74, ("amenity", "fuel")), Now);
        await Assert.That(poi).IsNull();
    }

    [Test]
    public async Task SeamarkHarbour_ClassifiesAsHarbour()
    {
        var poi = OverpassResponseParser.TryMapElement(
            Node(7, 35.0, 25.0, ("seamark:type", "harbour")), Now);
        await Assert.That(poi!.Category).IsEqualTo(MarinePoiCategory.Harbour);
    }

    [Test]
    public async Task MooringBuoy_ClassifiesAsMooring()
    {
        // seamark:type=mooring_buoy must classify as Mooring; the
        // builder uses ~"^mooring" regex and the parser must mirror
        // with StartsWith.
        var poi = OverpassResponseParser.TryMapElement(
            Node(8, 43.0, 5.0, ("seamark:type", "mooring_buoy")), Now);
        await Assert.That(poi!.Category).IsEqualTo(MarinePoiCategory.Mooring);
    }

    [Test]
    public async Task MarinaWithHarbourTag_PrefersMarina()
    {
        // Priority: a marina that is also a harbour reads as the
        // more informative Marina.
        var poi = OverpassResponseParser.TryMapElement(
            Node(9, 43.0, 5.0,
                ("leisure", "marina"),
                ("harbour", "yes")), Now);
        await Assert.That(poi!.Category).IsEqualTo(MarinePoiCategory.Marina);
    }

    [Test]
    public async Task MarinaOnPier_PrefersMarina()
    {
        // A marina built on a pier - man_made=pier + leisure=marina.
        // Parser priority order pins Marina ahead of Pier.
        var poi = OverpassResponseParser.TryMapElement(
            Node(10, 43.0, 5.0,
                ("leisure", "marina"),
                ("man_made", "pier")), Now);
        await Assert.That(poi!.Category).IsEqualTo(MarinePoiCategory.Marina);
    }

    [Test]
    public async Task ShipChandler_ClassifiesAsChandlery()
    {
        var poi = OverpassResponseParser.TryMapElement(
            Node(11, 43.0, 5.0, ("shop", "ship_chandler")), Now);
        await Assert.That(poi!.Category).IsEqualTo(MarinePoiCategory.Chandlery);
    }

    [Test]
    public async Task PumpOut_ClassifiesAsPumpOut()
    {
        var poi = OverpassResponseParser.TryMapElement(
            Node(12, 43.0, 5.0, ("waste_disposal", "marine")), Now);
        await Assert.That(poi!.Category).IsEqualTo(MarinePoiCategory.PumpOut);
    }

    [Test]
    public async Task DrinkingWater_AlsoTaggedFuel_PrefersFuel()
    {
        // A fuel dock sometimes carries amenity=drinking_water as a
        // sub-tag. Fuel is more specific (and what the helm wants to
        // see); ensure priority lands there.
        var poi = OverpassResponseParser.TryMapElement(
            Node(13, 43.0, 5.0,
                ("amenity", "fuel"),
                ("boat", "yes"),
                ("drinking_water", "yes")), Now);
        await Assert.That(poi!.Category).IsEqualTo(MarinePoiCategory.Fuel);
    }

    [Test]
    public async Task Way_UsesCenterCoordinate()
    {
        // Ways carry no direct lat/lon; only `out center` injects one.
        // Pin that the parser falls through .Center properly.
        var poi = OverpassResponseParser.TryMapElement(
            Way(20, 36.5, 14.3, ("leisure", "marina")), Now);
        await Assert.That(poi).IsNotNull();
        await Assert.That(poi!.Lat).IsEqualTo(36.5);
        await Assert.That(poi.Lon).IsEqualTo(14.3);
        await Assert.That(poi.Id).IsEqualTo("w20");
    }

    [Test]
    public async Task Element_NaNCoord_Rejected()
    {
        var poi = OverpassResponseParser.TryMapElement(
            Node(30, double.NaN, 5.0, ("leisure", "marina")), Now);
        await Assert.That(poi).IsNull();
    }

    [Test]
    public async Task Element_OutOfRangeLat_Rejected()
    {
        var poi = OverpassResponseParser.TryMapElement(
            Node(31, 200.0, 5.0, ("leisure", "marina")), Now);
        await Assert.That(poi).IsNull();
    }

    [Test]
    public async Task Element_NoTags_Rejected()
    {
        // No tags = nothing to classify -> drop the element.
        var poi = OverpassResponseParser.TryMapElement(
            new OverpassElement
            {
                Type = "node",
                Id = 40,
                Lat = 40,
                Lon = -74,
                Tags = null,
            }, Now);
        await Assert.That(poi).IsNull();
    }

    [Test]
    public async Task Element_UnknownType_Rejected()
    {
        // Non-node/way/relation type prefix is unmappable. Defends
        // against schema drift if Overpass ever adds a new element
        // type we haven't taught the parser about.
        var poi = OverpassResponseParser.TryMapElement(
            new OverpassElement
            {
                Type = "changeset",
                Id = 50,
                Lat = 40,
                Lon = -74,
                Tags = new Dictionary<string, string> { ["leisure"] = "marina" },
            }, Now);
        await Assert.That(poi).IsNull();
    }

    [Test]
    public async Task Parse_NullResponse_ReturnsEmpty()
    {
        var pois = OverpassResponseParser.Parse(null, Now);
        await Assert.That(pois.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_EmptyElements_ReturnsEmpty()
    {
        var pois = OverpassResponseParser.Parse(new OverpassResponse { Elements = [] }, Now);
        await Assert.That(pois.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_MixedShapes_ExtractsAndDropsConsistently()
    {
        // Realistic shape: a few good elements + a few junk ones.
        // Parser drops the junk silently and keeps the rest.
        var resp = new OverpassResponse
        {
            Elements =
            [
                Node(1, 40.0, -74.0, ("leisure", "marina")),
                Node(2, double.NaN, -74.0, ("leisure", "marina")),  // bad coord
                Node(3, 40.0, -74.0),                                  // no tags
                Way(4, 41.0, -73.0, ("amenity", "fuel"), ("boat", "yes")),
            ]
        };
        var pois = OverpassResponseParser.Parse(resp, Now);
        await Assert.That(pois.Count).IsEqualTo(2);
        await Assert.That(pois[0].Category).IsEqualTo(MarinePoiCategory.Marina);
        await Assert.That(pois[1].Category).IsEqualTo(MarinePoiCategory.Fuel);
    }

    [Test]
    public async Task ProjectPopupTags_KeepsWhitelistOnly()
    {
        // The popup-tag whitelist drops editor-only metadata so the
        // localStorage cache stays compact.
        var src = new Dictionary<string, string>
        {
            ["name"] = "Marina X",
            ["operator"] = "Acme",
            ["uid"] = "1234",            // editor metadata: drop
            ["addr:city"] = "Athens",     // not in whitelist: drop
            ["seamark:harbour:category"] = "marina",  // seamark:* kept
        };
        var kept = OverpassResponseParser.ProjectPopupTags(src);
        await Assert.That(kept.ContainsKey("name")).IsTrue();
        await Assert.That(kept.ContainsKey("operator")).IsTrue();
        await Assert.That(kept.ContainsKey("uid")).IsFalse();
        await Assert.That(kept.ContainsKey("addr:city")).IsFalse();
        await Assert.That(kept.ContainsKey("seamark:harbour:category")).IsTrue();
    }
}
