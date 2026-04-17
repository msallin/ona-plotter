namespace OnaPlotter.Utilities;

/// <summary>
/// COLREGS (power-driven vessel) encounter classifier. Takes own and target
/// position / COG / SOG and returns the collision-regulation category plus
/// the give-way role. Useful for a quick "who turns?" glance when a target
/// shows up on the guard zone.
/// <para>
/// We use the classical thresholds from Rules 13-15: an overtaking arc of
/// 22.5° either side of a reciprocal course for head-on, the same 22.5°
/// abaft-the-beam cut-off for overtaking, and side-of-own-beam for
/// crossings. Sailing-vessel rules (Rule 12, wind-based) are out of scope
/// here - this is the decision a motorised watch keeper makes in seconds.
/// </para>
/// </summary>
public static class Colregs
{
    public enum Category
    {
        /// <summary>Too slow / parallel / unknown - no determination.</summary>
        Indeterminate,
        /// <summary>Nearly reciprocal courses, target within ±22.5° of our bow.</summary>
        HeadOn,
        /// <summary>Same direction, we're catching up from abaft the target's beam.</summary>
        Overtaking,
        /// <summary>Same direction, target is catching up on us from our stern.</summary>
        BeingOvertaken,
        /// <summary>Target approaches from our port side on a crossing course.</summary>
        CrossingFromPort,
        /// <summary>Target approaches from our starboard side on a crossing course.</summary>
        CrossingFromStarboard,
    }

    public enum Role
    {
        /// <summary>No duty either way - the encounter hasn't resolved into a rule.</summary>
        None,
        /// <summary>We must keep clear (Rule 13, 15-stbd, 14 both).</summary>
        GiveWay,
        /// <summary>We hold course and speed (Rule 17).</summary>
        StandOn,
    }

    public readonly record struct Result(Category Category, Role Role);

    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;
    private const double HeadOnCone = 22.5;       // half-angle
    private const double OvertakingCone = 22.5;   // abaft the beam cut-off
    private const double StationaryMs = 0.1;      // ~0.2 kn

    public static Result Classify(
        double ownLat, double ownLon, double ownCogRad, double ownSogMs,
        double tgtLat, double tgtLon, double tgtCogRad, double tgtSogMs)
    {
        if (ownSogMs < StationaryMs && tgtSogMs < StationaryMs)
            return new Result(Category.Indeterminate, Role.None);

        // Absolute bearing from own to target (0..360 degrees, 0 = north).
        double bearing = BearingDeg(ownLat, ownLon, tgtLat, tgtLon);

        // Relative bearing: 0 = dead ahead, 90 = starboard beam, 180 = dead astern.
        double ownCogDeg = Norm360(ownCogRad * RadToDeg);
        double relBearing = Norm360(bearing - ownCogDeg);

        // Heading alignment: smallest angle between the two COGs (0..180).
        double tgtCogDeg = Norm360(tgtCogRad * RadToDeg);
        double headingDiff = SmallestAngle(ownCogDeg, tgtCogDeg);

        bool targetAhead = relBearing < HeadOnCone || relBearing > 360 - HeadOnCone;
        bool targetAstern = Math.Abs(relBearing - 180) < OvertakingCone;
        bool targetOnStbd = relBearing >= HeadOnCone && relBearing <= 180 - OvertakingCone;
        bool targetOnPort = relBearing >= 180 + OvertakingCone && relBearing <= 360 - HeadOnCone;

        // Head-on: target ahead AND heading roughly reciprocal.
        if (targetAhead && headingDiff > 180 - HeadOnCone)
            return new Result(Category.HeadOn, Role.GiveWay);

        // Overtaking / being overtaken: headings roughly aligned.
        if (headingDiff < OvertakingCone)
        {
            if (targetAhead && ownSogMs > tgtSogMs)
                return new Result(Category.Overtaking, Role.GiveWay);
            if (targetAstern && tgtSogMs > ownSogMs)
                return new Result(Category.BeingOvertaken, Role.StandOn);
        }

        // Crossing: target on one side of our beam, heading not aligned.
        if (targetOnStbd)
            return new Result(Category.CrossingFromStarboard, Role.GiveWay);
        if (targetOnPort)
            return new Result(Category.CrossingFromPort, Role.StandOn);

        return new Result(Category.Indeterminate, Role.None);
    }

    /// <summary>Human-readable short label for the popup / vessel list.</summary>
    public static string ShortLabel(Category c) => c switch
    {
        Category.HeadOn => "Head-on",
        Category.Overtaking => "Overtaking",
        Category.BeingOvertaken => "Being overtaken",
        Category.CrossingFromPort => "Crossing (port)",
        Category.CrossingFromStarboard => "Crossing (stbd)",
        _ => ""
    };

    public static string RoleLabel(Role r) => r switch
    {
        Role.GiveWay => "Give way",
        Role.StandOn => "Stand on",
        _ => ""
    };

    private static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        double lat1r = lat1 * DegToRad, lat2r = lat2 * DegToRad;
        double dLon = (lon2 - lon1) * DegToRad;
        double y = Math.Sin(dLon) * Math.Cos(lat2r);
        double x = Math.Cos(lat1r) * Math.Sin(lat2r) - Math.Sin(lat1r) * Math.Cos(lat2r) * Math.Cos(dLon);
        return Norm360(Math.Atan2(y, x) * RadToDeg);
    }

    private static double Norm360(double deg)
    {
        deg %= 360;
        return deg < 0 ? deg + 360 : deg;
    }

    private static double SmallestAngle(double a, double b)
    {
        double d = Math.Abs(Norm360(a - b));
        return d > 180 ? 360 - d : d;
    }
}
