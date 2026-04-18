using OnaPlotter.Models;

namespace OnaPlotter.Utilities;

/// <summary>
/// Classical isochrone weather router. At each time step, expand the
/// current boundary by sailing every feasible bearing for <c>dt</c>
/// minutes, look up the local wind, apply the boat polar, land on a
/// new point. Prune the expanded set by keeping only the furthest
/// reach per angular sector from the start - this is what stops the
/// candidate set growing exponentially.
///
/// <para>Uses a local equirectangular projection centred on the start
/// point. Accurate to well under one nautical mile for routes up to a
/// few hundred NM; the routing horizon is capped well before that.</para>
///
/// <para>The weather function is pluggable so the router has no runtime
/// dependencies - unit tests drive it with synthetic wind.</para>
/// </summary>
public static class IsochroneRouter
{
    public sealed record Options(
        double StepMinutes = 15,
        double BearingDegStep = 10,
        double AngularSectorDeg = 5,
        int MaxSteps = 48,                  // 48 * 15 min = 12 h default horizon
        double ReachNauticalMiles = 0.5,    // declared reached inside this radius
        double MinBoatSpeedKn = 0.2);       // ignore bearings that produce dead-stop polars

    /// <summary>
    /// Tries to compute the fastest route from <paramref name="startLat"/>/
    /// <paramref name="startLon"/> to <paramref name="endLat"/>/
    /// <paramref name="endLon"/> starting at <paramref name="startTime"/>.
    /// Returns null when the destination isn't reachable within the
    /// configured MaxSteps window.
    /// </summary>
    /// <param name="weather">(lat, lon, time) -&gt; wind (degrees TRUE FROM,
    /// knots). Return null to signal "no wind data, skip this candidate".</param>
    /// <param name="polar">(TWA degrees 0-180, TWS knots) -&gt; target boat
    /// speed knots, or null for no-go / out-of-range.</param>
    public static WeatherRoute? Route(
        double startLat, double startLon, DateTime startTime,
        double endLat, double endLon,
        Func<double, double, DateTime, WindSample?> weather,
        Func<double, double, double?> polar,
        Options? options = null)
    {
        var opt = options ?? new Options();
        double dtHours = opt.StepMinutes / 60.0;

        // Short-circuit: destination already within reach.
        double initialNm = DistanceNm(startLat, startLon, endLat, endLon);
        if (initialNm <= opt.ReachNauticalMiles)
        {
            var wp0 = new WeatherRouteWaypoint(startLat, startLon, startTime);
            var wp1 = new WeatherRouteWaypoint(endLat, endLon, startTime);
            return new WeatherRoute([wp0, wp1], initialNm, TimeSpan.Zero);
        }

        var nodes = new List<Node>
        {
            new Node(startLat, startLon, startTime, ParentIndex: -1)
        };
        var frontier = new List<int> { 0 };

        for (int step = 0; step < opt.MaxSteps; step++)
        {
            // --- 1. Partial-step reach check -----------------------------
            // Before expanding the full isochrone, see whether any frontier
            // point can reach the destination directly within a single dt.
            // Without this, destinations that fall between isochrone
            // boundaries get overshot and the algorithm never terminates
            // even when it's one broad reach away.
            foreach (int parentIdx in frontier)
            {
                var p = nodes[parentIdx];
                double distToEnd = DistanceNm(p.Lat, p.Lon, endLat, endLon);
                if (distToEnd > opt.ReachNauticalMiles + 0.01)
                {
                    double bearingToEnd = BearingDeg(p.Lat, p.Lon, endLat, endLon);
                    var windAtP = weather(p.Lat, p.Lon, p.Time);
                    if (windAtP is null) continue;
                    double twa = AbsoluteTwa(bearingToEnd, windAtP.Value.DirectionDeg);
                    double? spd = polar(twa, windAtP.Value.SpeedKn);
                    if (spd is null || spd < opt.MinBoatSpeedKn) continue;
                    double reachable = spd.Value * dtHours;
                    if (distToEnd > reachable) continue;

                    // Feasible: sail the final leg directly to the end.
                    double legHours = distToEnd / spd.Value;
                    DateTime arrival = p.Time.AddHours(legHours);
                    return Backtrack(nodes, parentIdx, endLat, endLon, arrival, startTime);
                }
                else
                {
                    // Already at destination.
                    return Backtrack(nodes, parentIdx, endLat, endLon, p.Time, startTime);
                }
            }

            // --- 2. Isochrone expansion ----------------------------------
            var expanded = new List<Node>();
            DateTime stepTime = startTime.AddMinutes(opt.StepMinutes * (step + 1));

            foreach (int parentIdx in frontier)
            {
                var p = nodes[parentIdx];
                var wind = weather(p.Lat, p.Lon, p.Time);
                if (wind is null) continue;

                for (double bearing = 0; bearing < 360; bearing += opt.BearingDegStep)
                {
                    double twa = AbsoluteTwa(bearing, wind.Value.DirectionDeg);
                    double? speed = polar(twa, wind.Value.SpeedKn);
                    if (speed is null || speed < opt.MinBoatSpeedKn) continue;

                    double distanceNm = speed.Value * dtHours;
                    var (nextLat, nextLon) = Advance(p.Lat, p.Lon, bearing, distanceNm);
                    expanded.Add(new Node(nextLat, nextLon, stepTime, parentIdx));
                }
            }

            if (expanded.Count == 0) return null;   // becalmed or stuck

            var pruned = PruneBySector(expanded, startLat, startLon, opt.AngularSectorDeg);

            int frontierStart = nodes.Count;
            foreach (var node in pruned) nodes.Add(node);
            frontier = Enumerable.Range(frontierStart, pruned.Count).ToList();
        }

        return null;
    }

    private static WeatherRoute Backtrack(
        List<Node> nodes, int fromIdx,
        double endLat, double endLon, DateTime arrivalTime,
        DateTime startTime)
    {
        var path = new List<WeatherRouteWaypoint>();
        for (int idx = fromIdx; idx >= 0; idx = nodes[idx].ParentIndex)
        {
            var n = nodes[idx];
            path.Add(new WeatherRouteWaypoint(n.Lat, n.Lon, n.Time));
            if (n.ParentIndex < 0) break;
        }
        path.Reverse();
        path.Add(new WeatherRouteWaypoint(endLat, endLon, arrivalTime));

        double totalNm = 0;
        for (int i = 1; i < path.Count; i++)
            totalNm += DistanceNm(path[i - 1].Latitude, path[i - 1].Longitude,
                                  path[i].Latitude, path[i].Longitude);
        return new WeatherRoute(path, totalNm, arrivalTime - startTime);
    }

    // --- internals ----------------------------------------------------

    private readonly record struct Node(double Lat, double Lon, DateTime Time, int ParentIndex);

    /// <summary>Angular TWA (0..180 deg) between own bearing and wind
    /// direction (wind blows FROM - subtract 180 to get "toward").</summary>
    private static double AbsoluteTwa(double ownBearingDeg, double windFromDeg)
    {
        double windTowardDeg = (windFromDeg + 180) % 360;
        double diff = Math.Abs(Norm360(ownBearingDeg - windTowardDeg + 180));
        return diff > 180 ? 360 - diff : diff;
    }

    /// <summary>Bin candidate nodes by their angular sector from the start,
    /// keep only the furthest per bin. Prevents exponential growth of the
    /// candidate set while preserving the envelope of best reaches.</summary>
    private static List<Node> PruneBySector(List<Node> nodes, double startLat, double startLon, double sectorDeg)
    {
        int bins = (int)Math.Ceiling(360 / sectorDeg);
        var bestPerBin = new Dictionary<int, (Node Node, double DistNm)>();
        foreach (var n in nodes)
        {
            double bearing = BearingDeg(startLat, startLon, n.Lat, n.Lon);
            int bin = (int)(bearing / sectorDeg) % bins;
            double dist = DistanceNm(startLat, startLon, n.Lat, n.Lon);
            if (!bestPerBin.TryGetValue(bin, out var cur) || dist > cur.DistNm)
                bestPerBin[bin] = (n, dist);
        }
        return bestPerBin.Values.Select(x => x.Node).ToList();
    }

    private const double NmPerDegLat = 60.0;

    private static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * NmPerDegLat;
        double dLon = (lon2 - lon1) * NmPerDegLat * Math.Cos((lat1 + lat2) * 0.5 * Math.PI / 180);
        return Math.Sqrt(dLat * dLat + dLon * dLon);
    }

    private static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1);
        double dLon = (lon2 - lon1) * Math.Cos((lat1 + lat2) * 0.5 * Math.PI / 180);
        double rad = Math.Atan2(dLon, dLat);
        return Norm360(rad * 180 / Math.PI);
    }

    private static (double Lat, double Lon) Advance(double lat, double lon, double bearingDeg, double distanceNm)
    {
        double brgRad = bearingDeg * Math.PI / 180;
        double dLat = Math.Cos(brgRad) * distanceNm / NmPerDegLat;
        double dLon = Math.Sin(brgRad) * distanceNm / (NmPerDegLat * Math.Cos((lat + dLat * 0.5) * Math.PI / 180));
        return (lat + dLat, lon + dLon);
    }

    private static double Norm360(double deg)
    {
        deg %= 360;
        return deg < 0 ? deg + 360 : deg;
    }
}
