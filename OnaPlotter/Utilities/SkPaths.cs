namespace OnaPlotter.Utilities;

/// <summary>
/// Centralised SignalK path-string constants. Lives here rather than
/// scattered across DTOs and switch cases so a typo in one place
/// (silent delta-ignore in production with no compile error) becomes
/// a compile-time mistake instead.
///
/// <para>Layout mirrors the SignalK path tree: nested static classes
/// match dot-separated path segments. Reading a call site as
/// <c>SkPaths.Navigation.Position</c> tracks the wire format
/// (<c>"navigation.position"</c>) one-for-one while staying typo-safe.</para>
///
/// <para>Only paths used in two or more files live here; one-shot
/// references stay inline at the use site since lifting them out
/// adds indirection without dedup payoff.</para>
/// </summary>
public static class SkPaths
{
    public static class Navigation
    {
        public const string Position = "navigation.position";
        public const string SpeedOverGround = "navigation.speedOverGround";
        public const string CourseOverGroundTrue = "navigation.courseOverGroundTrue";
        public const string CourseOverGroundMagnetic = "navigation.courseOverGroundMagnetic";
        public const string HeadingTrue = "navigation.headingTrue";
        public const string HeadingMagnetic = "navigation.headingMagnetic";
        public const string State = "navigation.state";

        public static class Anchor
        {
            public const string Position = "navigation.anchor.position";
            public const string CurrentRadius = "navigation.anchor.currentRadius";
            public const string MaxRadius = "navigation.anchor.maxRadius";
            // v2.0.0+ paths -- only published when the
            // signalk-anchoralarm-plugin is at least v2 and the helm
            // has the corresponding rode-counter / depth sensor wired.
            // Plumbing them all the way through means the HUD picks
            // the readouts up when they appear without a code change
            // landing first.
            public const string BearingTrue = "navigation.anchor.bearingTrue";
            public const string ApparentBearing = "navigation.anchor.apparentBearing";
            public const string RodeLength = "navigation.anchor.rodeLength";
            public const string DistanceFromBow = "navigation.anchor.distanceFromBow";
        }

        public static class Course
        {
            public const string ActiveRoute = "navigation.course.activeRoute";
            public const string ActiveRouteName = "navigation.course.activeRoute.name";
            public const string ActiveRouteHref = "navigation.course.activeRoute.href";
            public const string ActiveRoutePointIndex = "navigation.course.activeRoute.pointIndex";
            public const string ActiveRoutePointTotal = "navigation.course.activeRoute.pointTotal";
            public const string NextPoint = "navigation.course.nextPoint";
            public const string NextPointPosition = "navigation.course.nextPoint.position";
            public const string PreviousPointPosition = "navigation.course.previousPoint.position";

            public static class CalcValues
            {
                public const string BearingTrue = "navigation.course.calcValues.bearingTrue";
                public const string Distance = "navigation.course.calcValues.distance";
                public const string CrossTrackError = "navigation.course.calcValues.crossTrackError";
                public const string TimeToGo = "navigation.course.calcValues.timeToGo";
                public const string VelocityMadeGood = "navigation.course.calcValues.velocityMadeGood";
                public const string RouteDistance = "navigation.course.calcValues.route.distance";
                public const string RouteTimeToGo = "navigation.course.calcValues.route.timeToGo";
                public const string PerpendicularPassed = "navigation.course.calcValues.perpendicularPassed";
                public const string ArrivalCircleEntered = "navigation.course.calcValues.arrivalCircleEntered";
            }
        }
    }

    public static class Environment
    {
        public static class Wind
        {
            public const string AngleApparent = "environment.wind.angleApparent";
            public const string SpeedApparent = "environment.wind.speedApparent";
            public const string AngleTrueWater = "environment.wind.angleTrueWater";
            public const string SpeedTrue = "environment.wind.speedTrue";
            public const string DirectionTrue = "environment.wind.directionTrue";
        }
    }
}
