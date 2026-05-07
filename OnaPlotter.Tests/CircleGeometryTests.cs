using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Boundary coverage for <see cref="CircleGeometry.BuildRing"/>. The
/// happy path (mid-latitude, 500 m, 32 vertices) is pinned by
/// RegionApiTests; this suite covers pole safety, vertex-count
/// validation, southern hemisphere, and zero/negative radius.
/// </summary>
public class CircleGeometryTests
{
    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        double phi1 = lat1 * Math.PI / 180;
        double phi2 = lat2 * Math.PI / 180;
        double dPhi = (lat2 - lat1) * Math.PI / 180;
        double dLambda = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
                 + Math.Cos(phi1) * Math.Cos(phi2)
                 * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);
        return 2 * R * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    [Test]
    public async Task NorthPole_DoesNotProduceNaN()
    {
        // The pole-safety floor (1 m/deg-lon) is documented in the
        // class comment but un-tested. Confirm a ring at 90° N is
        // finite end-to-end - a NaN coordinate would slip past
        // GeoJSON validators and crash the renderer downstream.
        var ring = CircleGeometry.BuildRing(90.0, 0.0, 500, 32);

        await Assert.That(ring.Length).IsEqualTo(33);
        foreach (var v in ring)
        {
            await Assert.That(double.IsFinite(v[0])).IsTrue();
            await Assert.That(double.IsFinite(v[1])).IsTrue();
        }
    }

    [Test]
    public async Task SouthPole_DoesNotProduceNaN()
    {
        // Mirror test; the pole-safety branch must symmetrise.
        var ring = CircleGeometry.BuildRing(-90.0, 0.0, 500, 32);
        foreach (var v in ring)
        {
            await Assert.That(double.IsFinite(v[0])).IsTrue();
            await Assert.That(double.IsFinite(v[1])).IsTrue();
        }
    }

    [Test]
    public async Task SouthernHemisphere_StillCloses()
    {
        // Sanity at -47.4°: the sign of dLat shouldn't flip the close
        // logic. The first vertex must equal the last, regardless of
        // hemisphere.
        var ring = CircleGeometry.BuildRing(-47.4, 8.5, 500, 32);
        await Assert.That(ring[0][0]).IsEqualTo(ring[^1][0]);
        await Assert.That(ring[0][1]).IsEqualTo(ring[^1][1]);
    }

    [Test]
    public async Task ZeroRadius_AllVerticesEqualCentre()
    {
        // Degenerate but defined: a 0 m circle is a single point. All
        // vertices should sit on the centre, the ring still closes.
        var ring = CircleGeometry.BuildRing(47.4, 8.5, 0, 32);
        await Assert.That(ring.Length).IsEqualTo(33);
        foreach (var v in ring)
        {
            await Assert.That(v[0]).IsEqualTo(8.5);
            await Assert.That(v[1]).IsEqualTo(47.4);
        }
    }

    [Test]
    public async Task NegativeRadius_StillProducesFiniteRing()
    {
        // Negative radius mirrors the ring through the centre. Not a
        // useful semantic but the function should not crash; pin
        // "finite output" so a future "throw on negative" is a
        // deliberate decision, not an accidental refactor.
        var ring = CircleGeometry.BuildRing(47.4, 8.5, -500, 32);
        await Assert.That(ring.Length).IsEqualTo(33);
        foreach (var v in ring)
        {
            await Assert.That(double.IsFinite(v[0])).IsTrue();
            await Assert.That(double.IsFinite(v[1])).IsTrue();
        }
    }

    [Test]
    public async Task ZeroVertices_Throws()
    {
        // The close-the-ring step (ring[vertices] = ring[0]) reads
        // ring[0] which is null when no vertex was written. Guard at
        // the entry point with ArgumentOutOfRangeException so callers
        // see a meaningful error rather than NullReferenceException.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => { CircleGeometry.BuildRing(47.4, 8.5, 500, 0); await Task.CompletedTask; });
    }

    [Test]
    public async Task NegativeVertices_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => { CircleGeometry.BuildRing(47.4, 8.5, 500, -1); await Task.CompletedTask; });
    }

    [Test]
    public async Task TwoVertices_Throws()
    {
        // 1 and 2 vertices produce degenerate "circles" no consumer
        // wants - the same throw protects all sub-3 inputs.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => { CircleGeometry.BuildRing(47.4, 8.5, 500, 2); await Task.CompletedTask; });
    }

    [Test]
    public async Task EquatorialRing_RadiusToleranceWithinOnePercent()
    {
        // Equator is the "easy" latitude (cos=1, no projection
        // distortion). Every vertex should be within 1 % of the
        // requested radius.
        const double radius = 1000;
        var ring = CircleGeometry.BuildRing(0.0, 0.0, radius, 64);
        foreach (var v in ring)
        {
            double meters = HaversineMeters(0.0, 0.0, v[1], v[0]);
            double err = Math.Abs(meters - radius) / radius;
            await Assert.That(err < 0.01).IsTrue();
        }
    }

    [Test]
    public async Task GeoJsonOrder_LonComesFirst()
    {
        // GeoJSON order is [lon, lat]; the existing RegionApi pipeline
        // assumes this, so a vertex flip would corrupt every emitted
        // circle region. Pin by checking that a small radius keeps the
        // vertex's longitude near 0 and its latitude swings near the
        // centre lat.
        var ring = CircleGeometry.BuildRing(47.4, 8.5, 100, 4);
        // Vertex 0 (angle 0): pure-north offset, lon ~= 8.5.
        await Assert.That(Math.Abs(ring[0][0] - 8.5) < 0.001).IsTrue();
        await Assert.That(ring[0][1] > 47.4).IsTrue();
    }
}
