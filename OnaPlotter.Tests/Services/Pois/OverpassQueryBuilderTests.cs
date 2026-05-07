using OnaPlotter.Models;
using OnaPlotter.Services.Pois;

namespace OnaPlotter.Tests.Services.Pois;

/// <summary>
/// Pin the Overpass QL wire shape per category combination. The
/// builder is pure; we don't need any I/O. The contract this test
/// guards: a tag mapping change has to come through a deliberate
/// edit to a test fixture, not slip in as a "harmless" string tweak.
/// </summary>
public class OverpassQueryBuilderTests
{
    private static IReadOnlySet<MarinePoiCategory> Cat(params MarinePoiCategory[] cats)
        => new HashSet<MarinePoiCategory>(cats);

    [Test]
    public async Task Build_NoCategories_ReturnsNull()
    {
        // Empty set must short-circuit to null so the caller skips the
        // round-trip; otherwise the public Overpass instance gets a
        // useless ping every viewport change with categories off.
        var q = OverpassQueryBuilder.Build(0, 0, 1, 1, Cat());
        await Assert.That(q).IsNull();
    }

    [Test]
    public async Task Build_FuelOnly_RequiresBoatYesFilter()
    {
        // boat=yes is non-negotiable: without it, a fuel query in any
        // coastal-area bbox returns every road petrol station too.
        var q = OverpassQueryBuilder.Build(40, -75, 41, -74, Cat(MarinePoiCategory.Fuel));
        await Assert.That(q).IsNotNull();
        await Assert.That(q!).Contains("\"amenity\"=\"fuel\"");
        await Assert.That(q).Contains("\"boat\"=\"yes\"");
    }

    [Test]
    public async Task Build_BboxOrder_IsSouthWestNorthEast()
    {
        // Overpass takes (S,W,N,E) which is the OPPOSITE of Leaflet's
        // (W,S,E,N). The builder accepts Leaflet-style and re-orders;
        // a regression here would make every query miss the helm's
        // viewport and silently return nothing.
        var q = OverpassQueryBuilder.Build(
            south: 40.10, west: -74.20, north: 41.30, east: -73.40,
            Cat(MarinePoiCategory.Marina));
        await Assert.That(q!).Contains("(40.100000,-74.200000,41.300000,-73.400000)");
    }

    [Test]
    public async Task Build_AllCategories_MentionsEveryDistinctTag()
    {
        // Smoke test: a maximally enabled set must include the
        // primary tag for every category. Adding a new category to
        // the enum without updating the builder would silently send
        // a query that omits it; this assert makes that surface.
        var all = Cat(
            MarinePoiCategory.Fuel,
            MarinePoiCategory.Marina,
            MarinePoiCategory.Harbour,
            MarinePoiCategory.Mooring,
            MarinePoiCategory.Slipway,
            MarinePoiCategory.Pier,
            MarinePoiCategory.Chandlery,
            MarinePoiCategory.DrinkingWater,
            MarinePoiCategory.PumpOut);
        var q = OverpassQueryBuilder.Build(40, -75, 41, -74, all)!;

        await Assert.That(q).Contains("\"amenity\"=\"fuel\"");
        await Assert.That(q).Contains("\"leisure\"=\"marina\"");
        await Assert.That(q).Contains("\"harbour\"=\"yes\"");
        await Assert.That(q).Contains("\"seamark:type\"=\"harbour\"");
        await Assert.That(q).Contains("\"mooring\"=\"yes\"");
        await Assert.That(q).Contains("\"seamark:type\"~\"^mooring\"");
        await Assert.That(q).Contains("\"leisure\"=\"slipway\"");
        await Assert.That(q).Contains("\"man_made\"=\"pier\"");
        await Assert.That(q).Contains("\"shop\"=\"boat\"");
        await Assert.That(q).Contains("\"shop\"=\"ship_chandler\"");
        await Assert.That(q).Contains("\"amenity\"=\"drinking_water\"");
        await Assert.That(q).Contains("\"waste_disposal\"=\"marine\"");
        await Assert.That(q).Contains("\"pumpout\"=\"yes\"");
    }

    [Test]
    public async Task Build_CategoryOrder_IsStable()
    {
        // Two callers passing the same set must get byte-identical
        // queries - the cache key on the controller side is the
        // query string, and instability would invalidate cache hits
        // every time the helm toggled a checkbox off and on.
        var a = OverpassQueryBuilder.Build(40, -75, 41, -74,
            Cat(MarinePoiCategory.Marina, MarinePoiCategory.Fuel));
        var b = OverpassQueryBuilder.Build(40, -75, 41, -74,
            Cat(MarinePoiCategory.Fuel, MarinePoiCategory.Marina));
        await Assert.That(a).IsEqualTo(b);
    }

    [Test]
    public async Task Build_HeaderEmitsTimeoutAndOutCenter()
    {
        // The "out center tags" suffix is what gives ways/relations
        // a center coord; without it, the parser would drop every
        // non-node element. Pin it so a future tweak doesn't break
        // marina rendering.
        var q = OverpassQueryBuilder.Build(40, -75, 41, -74, Cat(MarinePoiCategory.Marina))!;
        await Assert.That(q).Contains("[out:json][timeout:25]");
        await Assert.That(q).Contains("out center tags;");
    }

    [Test]
    public async Task Build_BboxAtAntimeridian_KeepsLongitudeOrder()
    {
        // Overpass accepts longitudes outside ±180 in the QL bbox
        // form, but the helm-facing input is always Leaflet bounds
        // (which clamp to ±180). A bbox sitting on the antimeridian
        // (180°E -> -179°W) is still ordered W < E in Leaflet only
        // when the map crosses zero. We don't unwrap; we just
        // serialise what we got. This test pins the negative case so
        // a future "helpful" wrap doesn't regress.
        var q = OverpassQueryBuilder.Build(-1, 179, 1, -179, Cat(MarinePoiCategory.Marina))!;
        await Assert.That(q).Contains("(-1.000000,179.000000,1.000000,-179.000000)");
    }
}
