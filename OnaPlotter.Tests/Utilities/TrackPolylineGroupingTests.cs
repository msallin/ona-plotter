using OnaPlotter.Utilities;

namespace OnaPlotter.Tests.Utilities;

/// <summary>
/// Pins the bucket-grouping semantics of <see cref="TrackPolylineGrouping"/>
/// against the old JS-side <c>setColoredTrack</c> algorithm. The C# port
/// must produce byte-identical results so the speed-coloured track
/// renders the same on the chart after the move.
/// </summary>
public sealed class TrackPolylineGroupingTests
{
    [Test]
    public async Task EmptyInput_ReturnsEmptyArray()
    {
        var runs = TrackPolylineGrouping.BucketRuns([]);
        await Assert.That(runs.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SinglePoint_ReturnsEmptyArray()
    {
        // A single point cannot form a polyline; the renderer would
        // discard it anyway, so the grouping short-circuits.
        var runs = TrackPolylineGrouping.BucketRuns([[54.5, 11.2, 3.0]]);
        await Assert.That(runs.Length).IsEqualTo(0);
    }

    [Test]
    public async Task UniformBucket_ProducesOneRun()
    {
        // Three consecutive points all in bucket 0 (SOG < 1 m/s).
        // Result: a single run with all three coords, bucket 0.
        double[][] points =
        [
            [54.5, 11.2, 0.5],
            [54.6, 11.3, 0.4],
            [54.7, 11.4, 0.3],
        ];

        var runs = TrackPolylineGrouping.BucketRuns(points);

        await Assert.That(runs.Length).IsEqualTo(1);
        await Assert.That(runs[0].Bucket).IsEqualTo(0);
        await Assert.That(runs[0].Coords.Length).IsEqualTo(3);
        await Assert.That(runs[0].Coords[0][0]).IsEqualTo(54.5);
        await Assert.That(runs[0].Coords[2][1]).IsEqualTo(11.4);
    }

    [Test]
    public async Task BucketChange_BridgeCoordIsDuplicatedAtBoundary()
    {
        // Two points in bucket 0 (0.5 m/s) then two in bucket 2
        // (2.5 m/s, >= 2 threshold). Expected: two runs; the
        // transition coord appears as the LAST entry of run 0 AND
        // the FIRST entry of run 1 so the polylines visually connect.
        double[][] points =
        [
            [54.0, 11.0, 0.5],
            [54.1, 11.1, 0.5],
            [54.2, 11.2, 2.5],
            [54.3, 11.3, 2.5],
        ];

        var runs = TrackPolylineGrouping.BucketRuns(points);

        await Assert.That(runs.Length).IsEqualTo(2);
        await Assert.That(runs[0].Bucket).IsEqualTo(0);
        await Assert.That(runs[1].Bucket).IsEqualTo(2);

        // Run 0: [(54.0,11.0), (54.1,11.1), (54.2,11.2)] - the
        // transition point closes the run.
        await Assert.That(runs[0].Coords.Length).IsEqualTo(3);
        await Assert.That(runs[0].Coords[2][0]).IsEqualTo(54.2);

        // Run 1: [(54.2,11.2), (54.3,11.3)] - same transition point
        // opens the new run.
        await Assert.That(runs[1].Coords.Length).IsEqualTo(2);
        await Assert.That(runs[1].Coords[0][0]).IsEqualTo(54.2);
        await Assert.That(runs[1].Coords[0][1]).IsEqualTo(11.2);
        await Assert.That(runs[1].Coords[1][0]).IsEqualTo(54.3);
    }

    [Test]
    public async Task TerminalBucketChange_DropsDegenerateFinalRun()
    {
        // Two points in bucket 0, then the very last point flips to
        // bucket 2. Expected: a single run for bucket 0 (with the
        // transition coord at the end), and NO trailing bucket-2 run
        // because that run would have a single coord (degenerate).
        // Mirrors the JS `if (runCoords.length >= 2)` guard on the
        // final flush.
        double[][] points =
        [
            [54.0, 11.0, 0.5],
            [54.1, 11.1, 0.5],
            [54.2, 11.2, 2.5],
        ];

        var runs = TrackPolylineGrouping.BucketRuns(points);

        await Assert.That(runs.Length).IsEqualTo(1);
        await Assert.That(runs[0].Bucket).IsEqualTo(0);
        await Assert.That(runs[0].Coords.Length).IsEqualTo(3);
    }

    [Test]
    public async Task ThreeBuckets_ProducesThreeRunsWithBridgeCoords()
    {
        // Algorithm quirk worth pinning: the FIRST point's SOG is
        // ignored. Each polyline segment is coloured by the bucket
        // of its terminal point (the leg's destination), so points[0]
        // contributes only its lat/lon as the trail's starting coord;
        // the bucket seed comes from points[1]'s SOG. To exercise
        // three distinct runs the inputs need: a seed leg, two
        // transitions, and a final same-bucket continuation so the
        // last run has >= 2 coords (a single-coord final run is
        // dropped as degenerate, see TerminalBucketChange test above).
        double[][] points =
        [
            [54.0, 11.0, 0.5],   // coord only (SOG ignored - first point)
            [54.1, 11.1, 1.5],   // bucket 1: seeds runBucket
            [54.2, 11.2, 3.5],   // bucket 3: transition 1
            [54.3, 11.3, 5.5],   // bucket 4: transition 2
            [54.4, 11.4, 5.5],   // bucket 4: continues so final run isn't degenerate
        ];

        var runs = TrackPolylineGrouping.BucketRuns(points);

        await Assert.That(runs.Length).IsEqualTo(3);
        await Assert.That(runs[0].Bucket).IsEqualTo(1);
        await Assert.That(runs[1].Bucket).IsEqualTo(3);
        await Assert.That(runs[2].Bucket).IsEqualTo(4);

        // Adjacent-run boundary identity: last coord of run[i] equals
        // first coord of run[i+1] for every transition.
        await Assert.That(runs[0].Coords[^1][0]).IsEqualTo(runs[1].Coords[0][0]);
        await Assert.That(runs[1].Coords[^1][0]).IsEqualTo(runs[2].Coords[0][0]);
    }
}
