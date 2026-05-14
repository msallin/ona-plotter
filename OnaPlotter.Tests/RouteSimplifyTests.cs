using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the contract of the RDP polyline simplifier used by the
/// "save trip as route" path. Behaviour invariants checked:
/// endpoints always survive, collinear runs collapse, sharp turns
/// are preserved, tolerance scales the aggression. The realistic-
/// track test guards against a regression where a future refactor
/// silently halves the reduction ratio.
/// </summary>
public class RouteSimplifyTests
{
    [Test]
    public async Task Rdp_Empty_ReturnsEmpty()
    {
        var result = RouteSimplify.Rdp([], 10);
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task Rdp_SinglePoint_ReturnsSingle()
    {
        var result = RouteSimplify.Rdp([[47.4, 8.5]], 10);
        await Assert.That(result.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Rdp_TwoPoints_ReturnsBoth()
    {
        var result = RouteSimplify.Rdp([[47.4, 8.5], [47.5, 8.6]], 10);
        await Assert.That(result.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Rdp_StraightLine_CollapsesToEndpoints()
    {
        // 11 collinear points along a north-east diagonal. Any non-
        // zero tolerance should drop every intermediate point because
        // the perpendicular distance is mathematically zero.
        var input = new List<double[]>();
        for (int i = 0; i <= 10; i++)
        {
            input.Add([47.4 + 0.001 * i, 8.5 + 0.001 * i]);
        }

        var result = RouteSimplify.Rdp(input, 1.0);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0][0]).IsEqualTo(47.4);
        await Assert.That(result[^1][0]).IsEqualTo(47.41);
    }

    [Test]
    public async Task Rdp_RightAngleTurn_PreservesCorner()
    {
        // Three eastbound points then three northbound. The shared
        // corner sits ~100 m off the diagonal between the endpoints,
        // well above a 5 m tolerance, so RDP must keep it.
        List<double[]> input =
        [
            [47.4000, 8.5000],
            [47.4000, 8.5005],
            [47.4000, 8.5010],
            [47.4000, 8.5015],  // corner
            [47.4005, 8.5015],
            [47.4010, 8.5015],
        ];

        var result = RouteSimplify.Rdp(input, 5.0);

        await Assert.That(result.Count).IsGreaterThanOrEqualTo(3);
        // First / last always survive; the corner is somewhere in the
        // middle.
        bool keptCorner = result.Any(p => p[0] == 47.4000 && p[1] == 8.5015);
        await Assert.That(keptCorner).IsTrue();
    }

    [Test]
    public async Task Rdp_HighTolerance_CollapsesZigzagToEndpoints()
    {
        // Tiny zigzag (~1 m amplitude) under a 100 m tolerance: every
        // intermediate point is well within tolerance of the endpoint
        // line, so output is just first + last.
        List<double[]> input =
        [
            [47.4000, 8.5000],
            [47.40001, 8.50001],
            [47.40002, 8.50000],
            [47.40003, 8.50001],
            [47.40004, 8.50000],
        ];

        var result = RouteSimplify.Rdp(input, 100.0);

        await Assert.That(result.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Rdp_ZeroTolerance_KeepsNonCollinearPoints()
    {
        // A point 22 km off the start-end line: any non-collinear
        // sample must survive at zero tolerance.
        List<double[]> input =
        [
            [47.4, 8.5],
            [47.5, 8.7],
            [47.6, 8.5],
        ];

        var result = RouteSimplify.Rdp(input, 0);

        await Assert.That(result.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Rdp_TenMeterToleranceOnGpsNoiseTrack_ReducesAtLeast70Percent()
    {
        // Realistic motoring scenario: a near-straight 1 nm leg sampled
        // every ~10 m with ~3 m of zero-mean Gaussian-ish noise (modelled
        // here as a deterministic sinusoid for reproducibility). At 10 m
        // tolerance the helm expects the noise to collapse aggressively
        // - a small handful of waypoints should suffice.
        var input = new List<double[]>();
        const int n = 200;
        // 1 nm east of 47.4 / 8.5 ~= +0.027 deg lon at that latitude.
        for (int i = 0; i < n; i++)
        {
            double frac = i / (double)(n - 1);
            double lon = 8.5 + 0.027 * frac;
            // Add a 3 m perpendicular wiggle (~ +/-0.00003 deg lat).
            double wiggle = 0.000027 * Math.Sin(i * 0.7);
            input.Add([47.4 + wiggle, lon]);
        }

        var result = RouteSimplify.Rdp(input, 10.0);

        // 70 % is the floor we want to guarantee; in practice this
        // scenario reduces to under 10 points (>95 %).
        double ratio = 1.0 - (result.Count / (double)n);
        await Assert.That(ratio).IsGreaterThan(0.70);
        // Endpoints must survive untouched.
        await Assert.That(result[0]).IsEquivalentTo(input[0]);
        await Assert.That(result[^1]).IsEquivalentTo(input[^1]);
    }

    [Test]
    public async Task Rdp_NegativeTolerance_Throws()
    {
        await Assert.That(() => RouteSimplify.Rdp([[0, 0], [1, 1]], -1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Rdp_NaNTolerance_Throws()
    {
        await Assert.That(() => RouteSimplify.Rdp([[0, 0], [1, 1]], double.NaN))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Rdp_DoesNotMutateInput()
    {
        List<double[]> input =
        [
            [47.4, 8.5],
            [47.5, 8.6],
            [47.6, 8.7],
        ];

        _ = RouteSimplify.Rdp(input, 10);

        await Assert.That(input.Count).IsEqualTo(3);
    }
}
