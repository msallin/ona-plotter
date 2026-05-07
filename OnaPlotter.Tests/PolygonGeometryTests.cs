using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the shoelace-on-equirectangular polygon area formula. The JS
/// renderer used to own this; the formula is a policy decision (we
/// trade a small geodesic error for a 30x faster computation) so it
/// belongs in C# alongside the other geometry helpers.
/// </summary>
public class PolygonGeometryTests
{
    [Test]
    public async Task Area_NullCoords_ReturnsZero()
    {
        await Assert.That(PolygonGeometry.AreaSquareMeters(null!)).IsEqualTo(0);
    }

    [Test]
    public async Task Area_EmptyCoords_ReturnsZero()
    {
        await Assert.That(PolygonGeometry.AreaSquareMeters(Array.Empty<double[]>())).IsEqualTo(0);
    }

    [Test]
    public async Task Area_FewerThanThreeVertices_ReturnsZero()
    {
        // Single point and pair-of-points cannot enclose area; the
        // panel reads "n vertices" without an area suffix in this case.
        var single = new double[][] { new[] { 47.0, 8.0 } };
        var pair = new double[][] { new[] { 47.0, 8.0 }, new[] { 47.0, 8.001 } };
        await Assert.That(PolygonGeometry.AreaSquareMeters(single)).IsEqualTo(0);
        await Assert.That(PolygonGeometry.AreaSquareMeters(pair)).IsEqualTo(0);
    }

    [Test]
    public async Task Area_TriangleAtEquator_MatchesAnalyticArea()
    {
        // 1 deg lon at the equator = 111_320 m; right-triangle with legs
        // of 1 deg lat (111_320 m) and 1 deg lon should be half base * height
        // = 1/2 * 111_320 * 111_320 ~= 6.196e9 m^2.
        var tri = new double[][]
        {
            new[] { 0.0, 0.0 },
            new[] { 0.0, 1.0 },
            new[] { 1.0, 0.0 }
        };
        double expected = 0.5 * 111_320.0 * 111_320.0;
        double actual = PolygonGeometry.AreaSquareMeters(tri);
        // 0.1% tolerance; equirectangular has zero error along the
        // equator anchor so this is exact bar floating-point noise.
        await Assert.That(Math.Abs(actual - expected) / expected).IsLessThan(0.001);
    }

    [Test]
    public async Task Area_OneHectareSquare_ApproxOneHectare()
    {
        // Realistic anchorage scale: a 100 m x 100 m square = 10_000 m^2 = 1 ha.
        // At 47 deg lat (Lake Constance latitude), one degree of longitude
        // is ~75_900 m. 100 m east = 100 / 75_900 = ~0.001317 deg lon.
        // 100 m north = 100 / 111_320 = ~0.000898 deg lat.
        const double lat0 = 47.0;
        const double lon0 = 8.0;
        double cosLat = Math.Cos(lat0 * Math.PI / 180.0);
        double dLon = 100.0 / (111_320.0 * cosLat);
        double dLat = 100.0 / 111_320.0;
        var square = new double[][]
        {
            new[] { lat0, lon0 },
            new[] { lat0, lon0 + dLon },
            new[] { lat0 + dLat, lon0 + dLon },
            new[] { lat0 + dLat, lon0 }
        };
        double area = PolygonGeometry.AreaSquareMeters(square);
        // 1 ha within 0.5%; the equirectangular projection is anchored
        // at the first vertex's lat so the small dLat doesn't shift cosLat.
        await Assert.That(Math.Abs(area - 10_000.0)).IsLessThan(50.0);
    }

    [Test]
    public async Task Area_WindingOrder_DoesNotChangeMagnitude()
    {
        // Helms drawing clockwise vs counter-clockwise both deserve a
        // positive area metric. The signed shoelace is taken absolute
        // before return - pin the contract.
        var ccw = new double[][]
        {
            new[] { 47.0, 8.0 },
            new[] { 47.0, 8.01 },
            new[] { 47.01, 8.01 },
            new[] { 47.01, 8.0 }
        };
        var cw = new double[][]
        {
            new[] { 47.0, 8.0 },
            new[] { 47.01, 8.0 },
            new[] { 47.01, 8.01 },
            new[] { 47.0, 8.01 }
        };
        double a1 = PolygonGeometry.AreaSquareMeters(ccw);
        double a2 = PolygonGeometry.AreaSquareMeters(cw);
        await Assert.That(Math.Abs(a1 - a2)).IsLessThan(0.001);
        await Assert.That(a1).IsGreaterThan(0);
    }

    [Test]
    public async Task Area_ImplicitClose_FirstAndLastVertexNeedNotMatch()
    {
        // The panel does not repeat the opening point; (i + 1) % n closes
        // the ring implicitly. Pin so a future caller doesn't have to
        // remember to append a duplicate.
        var openTriangle = new double[][]
        {
            new[] { 0.0, 0.0 },
            new[] { 0.0, 1.0 },
            new[] { 1.0, 0.0 }
        };
        // Same triangle with an explicit close also computed correctly
        // (the shoelace adds a zero-area edge from last->first->first).
        var closedTriangle = new double[][]
        {
            new[] { 0.0, 0.0 },
            new[] { 0.0, 1.0 },
            new[] { 1.0, 0.0 },
            new[] { 0.0, 0.0 }
        };
        double aOpen = PolygonGeometry.AreaSquareMeters(openTriangle);
        double aClosed = PolygonGeometry.AreaSquareMeters(closedTriangle);
        // The closed form has a degenerate edge that adds zero area;
        // both should match within floating-point noise.
        await Assert.That(Math.Abs(aOpen - aClosed) / aOpen).IsLessThan(0.001);
    }

    [Test]
    public async Task Area_ScalesByCosLat_PolarRegionIsSmaller()
    {
        // Same lon span at 70 deg lat covers a much smaller real distance
        // than at the equator (cos(70) = 0.342). A 1-deg-lon-wide
        // rectangle at 70 deg lat should be ~34% the area of the same
        // rectangle on the equator. Pin so a future regression where
        // the cosine factor is dropped (or hard-coded for one hemisphere)
        // surfaces immediately.
        var equatorRect = new double[][]
        {
            new[] { 0.0, 0.0 },
            new[] { 0.1, 0.0 },
            new[] { 0.1, 1.0 },
            new[] { 0.0, 1.0 }
        };
        var polarRect = new double[][]
        {
            new[] { 70.0, 0.0 },
            new[] { 70.1, 0.0 },
            new[] { 70.1, 1.0 },
            new[] { 70.0, 1.0 }
        };
        double aEq = PolygonGeometry.AreaSquareMeters(equatorRect);
        double aPolar = PolygonGeometry.AreaSquareMeters(polarRect);
        double ratio = aPolar / aEq;
        // cos(70 deg) = 0.342; tolerate 1% drift for the small dLat.
        await Assert.That(Math.Abs(ratio - 0.342)).IsLessThan(0.01);
    }

    [Test]
    public async Task Area_DegenerateNullVertex_IsSkipped()
    {
        // Defensive: a null entry inside the coord array (or a too-short
        // pair) used to be possible during a fast re-poll where the
        // editor was mid-splice. The C# side filters them; pin the
        // contract.
        var arr = new double[][]
        {
            new[] { 0.0, 0.0 },
            null!,
            new[] { 0.0, 1.0 },
            new[] { 1.0, 0.0 }
        };
        // The null vertex would otherwise crash the indexer; the helper
        // skips it. Result is well-defined (the remaining three points
        // form a triangle, with the skipped pair contributing nothing).
        double area = PolygonGeometry.AreaSquareMeters(arr);
        await Assert.That(area).IsGreaterThan(0);
    }
}
