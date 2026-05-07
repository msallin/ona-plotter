using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Property-based fuzz tests for <see cref="RouteProgress"/>. The HUD's
/// "Route total" line + the renderer's passed/planned split both rely on
/// these helpers, so a regression that returns a negative distance or an
/// out-of-range index silently breaks the chart UI.
/// </summary>
public class RouteProgressFuzzTests
{
    private const int Iterations = 1500;

    // Deterministic seed so fuzz failures are reproducible.
    private const int FixedSeed = 0x12345;

    [Test]
    public async Task FindClosestWaypointIndex_AlwaysReturnsValidIndex()
    {
        // Result must be in [0, coords.Length). Empty input returns 0.
        // Null inner rows are skipped (the helper guards p?.Length < 2).
        // Pin so a refactor that, e.g., starts the index at -1 sentinel
        // doesn't silently feed into the renderer.
        var rng = new Random(FixedSeed);
        for (int i = 0; i < Iterations; i++)
        {
            int n = rng.Next(0, 50);
            var coords = new double[n][];
            for (int j = 0; j < n; j++)
            {
                if (rng.Next(20) == 0) { coords[j] = null!; continue; }
                if (rng.Next(20) == 0) { coords[j] = new[] { rng.NextDouble() }; continue; }
                coords[j] = new[]
                {
                    rng.NextDouble() * 160.0 - 80.0,
                    rng.NextDouble() * 360.0 - 180.0,
                };
            }
            double lat = rng.NextDouble() * 160.0 - 80.0;
            double lon = rng.NextDouble() * 360.0 - 180.0;

            int idx = RouteProgress.FindClosestWaypointIndex(coords, lat, lon);
            await Assert.That(idx).IsGreaterThanOrEqualTo(0);
            // Empty coords returns 0; non-empty must yield index < length.
            if (n > 0) await Assert.That(idx).IsLessThan(n);
        }
    }

    [Test]
    public async Task FindClosestWaypointIndex_PicksNearestUnderJitter()
    {
        // For a known route with one obvious closest point, random jitter
        // around that point should still yield the same index. Catches a
        // regression where the equirectangular dx/dy axes get swapped.
        var rng = new Random(FixedSeed ^ 1);
        var coords = new double[][]
        {
            [40.0, -70.0],
            [41.0, -71.0],     // target
            [42.0, -72.0],
            [43.0, -73.0],
        };
        for (int i = 0; i < 500; i++)
        {
            double lat = 41.0 + (rng.NextDouble() - 0.5) * 0.05;
            double lon = -71.0 + (rng.NextDouble() - 0.5) * 0.05;
            int idx = RouteProgress.FindClosestWaypointIndex(coords, lat, lon);
            await Assert.That(idx).IsEqualTo(1);
        }
    }

    [Test]
    public async Task HaversineMeters_NonNegativeFiniteSymmetric()
    {
        // Distance is non-negative, finite, and symmetric (a->b == b->a).
        // The mean Earth radius constant is fixed; symmetry is the math
        // invariant the renderer + ETA helpers rely on.
        var rng = new Random(FixedSeed ^ 2);
        for (int i = 0; i < Iterations; i++)
        {
            double lat1 = rng.NextDouble() * 160.0 - 80.0;
            double lon1 = rng.NextDouble() * 360.0 - 180.0;
            double lat2 = rng.NextDouble() * 160.0 - 80.0;
            double lon2 = rng.NextDouble() * 360.0 - 180.0;

            double d1 = RouteProgress.HaversineMeters(lat1, lon1, lat2, lon2);
            double d2 = RouteProgress.HaversineMeters(lat2, lon2, lat1, lon1);

            await Assert.That(double.IsFinite(d1)).IsTrue();
            await Assert.That(d1).IsGreaterThanOrEqualTo(0.0);
            // 1 mm tolerance for the rounding noise of two sin/cos passes.
            await Assert.That(Math.Abs(d1 - d2)).IsLessThan(1e-3);
        }
    }

    [Test]
    public async Task HaversineMeters_IdenticalPointsReturnZero()
    {
        // a == b should yield exactly 0. Numerical jitter near a = b
        // (the asin(sqrt(0)) = 0 path) must round-trip exactly.
        var rng = new Random(FixedSeed ^ 3);
        for (int i = 0; i < 200; i++)
        {
            double lat = rng.NextDouble() * 160.0 - 80.0;
            double lon = rng.NextDouble() * 360.0 - 180.0;
            double d = RouteProgress.HaversineMeters(lat, lon, lat, lon);
            // 0.001 mm: tiny floating-point dust acceptable.
            await Assert.That(d).IsLessThan(1e-6);
        }
    }

    [Test]
    public async Task HaversineMeters_TriangleInequality()
    {
        // d(A, C) <= d(A, B) + d(B, C) for any three points. This is
        // the basic axiom of any distance metric; a regression that
        // swaps dy/dx or drops a sin/cos would break it on
        // long-baseline cases. Allow 1 m slack for the spherical-Earth
        // approximation's curvature error on multi-thousand-km legs.
        var rng = new Random(FixedSeed ^ 9);
        for (int i = 0; i < 1000; i++)
        {
            double[] a = [rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0];
            double[] b = [rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0];
            double[] c = [rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0];

            double ab = RouteProgress.HaversineMeters(a[0], a[1], b[0], b[1]);
            double bc = RouteProgress.HaversineMeters(b[0], b[1], c[0], c[1]);
            double ac = RouteProgress.HaversineMeters(a[0], a[1], c[0], c[1]);

            await Assert.That(ac).IsLessThanOrEqualTo(ab + bc + 1.0);
        }
    }

    [Test]
    public async Task TotalDistanceMeters_EqualsSumOfLegHaversines()
    {
        // Pin the contract: TotalDistanceMeters == sum(HaversineMeters
        // over consecutive pairs). A regression that switched to chord
        // length or skipped the last leg silently halves the route.
        var rng = new Random(FixedSeed ^ 4);
        for (int i = 0; i < 200; i++)
        {
            int n = rng.Next(2, 30);
            var coords = new double[n][];
            for (int j = 0; j < n; j++)
            {
                coords[j] = new[]
                {
                    rng.NextDouble() * 60.0 - 30.0,
                    rng.NextDouble() * 60.0 - 30.0,
                };
            }
            double total = RouteProgress.TotalDistanceMeters(coords);
            double sum = 0;
            for (int j = 1; j < n; j++)
                sum += RouteProgress.HaversineMeters(
                    coords[j - 1][0], coords[j - 1][1],
                    coords[j][0],     coords[j][1]);
            // 1 ppm tolerance for round-off across both paths.
            await Assert.That(Math.Abs(total - sum)).IsLessThan(Math.Max(1e-3, total * 1e-9));
        }
    }

    [Test]
    public async Task TotalDistanceMeters_DegenerateInputs_ReturnZero()
    {
        // Null / single-point / bad-row coords -> 0. Renderer relies on
        // this so a partial route doesn't trip the "Route total" line
        // into NaN or a partial sum.
        await Assert.That(RouteProgress.TotalDistanceMeters(null!)).IsEqualTo(0);
        await Assert.That(RouteProgress.TotalDistanceMeters([])).IsEqualTo(0);
        await Assert.That(RouteProgress.TotalDistanceMeters([[1.0, 2.0]])).IsEqualTo(0);

        var withGap = new double[][] { [1.0, 2.0], null!, [3.0, 4.0] };
        // The middle row is null; the helper skips both legs that touch it.
        await Assert.That(RouteProgress.TotalDistanceMeters(withGap)).IsEqualTo(0);
    }

    [Test]
    public async Task ResolveLegIndex_PrefersServerPointIndex()
    {
        // SK course engine's pointIndex is authoritative; fall-back is
        // only when missing. Pin so a refactor that "improves" by
        // recomputing the index even when the server has it doesn't
        // silently revert to the closest-waypoint heuristic on every
        // page load.
        var rng = new Random(FixedSeed ^ 5);
        for (int i = 0; i < 500; i++)
        {
            int serverIdx = rng.Next(0, 10);
            int n = rng.Next(2, 15);
            var coords = new double[n][];
            for (int j = 0; j < n; j++)
                coords[j] = [rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0];
            // nextLat / nextLon nowhere near serverIdx - if the helper
            // falls back to FindClosest it'd pick a different index.
            double nextLat = 0.0, nextLon = 0.0;

            var idx = RouteProgress.ResolveLegIndex(serverIdx, coords, nextLat, nextLon);
            await Assert.That(idx).IsEqualTo(serverIdx);
        }
    }

    [Test]
    public async Task ResolveLegIndex_NegativeServerIndex_FallsBack()
    {
        // pointIndex < 0 is a server bug or a not-started course. The
        // fallback should kick in (FindClosest if coords + nextPos are
        // present, otherwise null). Pin so a flipped condition (>=0
        // becomes >0) doesn't silently accept negative indices.
        var coords = new double[][] { [1.0, 1.0], [2.0, 2.0], [3.0, 3.0] };
        var idx = RouteProgress.ResolveLegIndex(-1, coords, 2.0, 2.0);
        await Assert.That(idx).IsEqualTo(1);

        var idxNoCoords = RouteProgress.ResolveLegIndex(-5, null, 1.0, 1.0);
        await Assert.That(idxNoCoords).IsNull();
    }
}
