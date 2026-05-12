using System.Text.Json.Serialization;

namespace OnaPlotter.Utilities;

/// <summary>
/// One contiguous run of own-track points that share the same speed
/// bucket. The JS renderer emits a single Leaflet polyline per run
/// coloured by <see cref="Bucket"/>; the grouping used to live in
/// JS but moves here so the per-point comparison + bridge-coord
/// duplication runs once in WASM rather than every time the renderer
/// is fed.
/// </summary>
public sealed class TrackPolylineRun
{
    /// <summary>Speed-bucket index into <see cref="SpeedBuckets.Buckets"/>.
    /// JS reads SPEED_BUCKETS[bucket] to look up the threshold metres-
    /// per-second value, then passes that into <c>speedColor()</c>.</summary>
    [JsonPropertyName("bucket")] public int Bucket { get; init; }

    /// <summary>Run coordinates as <c>[lat, lon]</c> pairs. Adjacent
    /// runs share a bridge coord (the first entry of a new run equals
    /// the last entry of the previous run) so the speed-bucket
    /// transition is visible as a continuous polyline transition
    /// rather than a gap.</summary>
    [JsonPropertyName("coords")] public double[][] Coords { get; init; } = [];
}

/// <summary>
/// Walks a track-buffer snapshot (lat / lon / SOG triples) and emits
/// one <see cref="TrackPolylineRun"/> per contiguous same-bucket span.
/// Mirrors what <c>setColoredTrack</c> in leafletInterop.js used to do
/// in JS, with the bridge-coord semantics preserved exactly:
/// <list type="bullet">
/// <item><description>The very first point seeds the current run.</description></item>
/// <item><description>On a bucket change the current run is flushed
/// AND the transition coord is also pushed onto the next run's first
/// slot so the polyline boundary is continuous.</description></item>
/// <item><description>The final run is flushed iff it has at least
/// two coords (a single-point trailing run would produce a degenerate
/// polyline).</description></item>
/// </list>
/// </summary>
public static class TrackPolylineGrouping
{
    /// <summary>
    /// Group consecutive same-bucket points into runs. Input is the
    /// raw wire format already in use elsewhere (<c>[lat, lon, sog]</c>
    /// triples). Empty / single-point input yields an empty array -
    /// the JS renderer no-ops a polyline with fewer than two points
    /// anyway.
    /// </summary>
    public static TrackPolylineRun[] BucketRuns(IReadOnlyList<double[]> points)
    {
        if (points is null || points.Count < 2) return [];

        var runs = new List<TrackPolylineRun>();
        // Match the JS algorithm: bucket of the FIRST transition (i=1)
        // seeds the run, and points[0] is the run's initial coord. A
        // single uniform-speed track therefore produces exactly one
        // run with N coords.
        int runBucket = SpeedBuckets.Bucket(points[1][2]);
        var runCoords = new List<double[]> { new[] { points[0][0], points[0][1] } };

        for (int i = 1; i < points.Count; i++)
        {
            int b = SpeedBuckets.Bucket(points[i][2]);
            double[] coord = new[] { points[i][0], points[i][1] };

            if (b != runBucket)
            {
                // Bridge: the new coord closes the previous run AND
                // opens the next so the polyline transition is
                // visually continuous (no chart gap at the bucket
                // boundary). Matches the JS-side runCoords.push(coord)
                // before flush + runCoords = [coord] after.
                runCoords.Add(coord);
                runs.Add(new TrackPolylineRun
                {
                    Bucket = runBucket,
                    Coords = runCoords.ToArray(),
                });
                runBucket = b;
                runCoords = [coord];
            }
            else
            {
                runCoords.Add(coord);
            }
        }
        // Final run flushes only if it has at least two coords. A
        // single coord (the bridge point of a bucket change at the
        // last index) doesn't form a drawable polyline; mirrors the
        // JS guard `if (runCoords.length >= 2)`.
        if (runCoords.Count >= 2)
        {
            runs.Add(new TrackPolylineRun
            {
                Bucket = runBucket,
                Coords = runCoords.ToArray(),
            });
        }
        return runs.ToArray();
    }
}
