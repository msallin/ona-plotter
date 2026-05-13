using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Tests;

public class NavigationDataTests
{
    [Test]
    [Arguments("navigation.speedOverGround", 3.5)]
    [Arguments("navigation.courseOverGroundTrue", 1.2)]
    [Arguments("navigation.headingTrue", 0.78)]
    [Arguments("environment.depth.belowTransducer", 12.4)]
    [Arguments("environment.wind.angleApparent", -0.5)]
    [Arguments("environment.wind.speedApparent", 7.2)]
    public async Task Apply_RecognizedPath_ReturnsTrue(string path, double value)
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(value);
        await Assert.That(nav.Apply(path, je)).IsTrue();
    }

    [Test]
    public async Task Apply_UnknownPath_ReturnsFalse()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(1.0);
        await Assert.That(nav.Apply("some.unknown.path", je)).IsFalse();
    }

    [Test]
    public async Task HeadingTrueResolved_PrefersHeadingTrue()
    {
        // Both true and magnetic published: true wins regardless of
        // the helm's PreferMagneticHeading display toggle. Radar
        // overlay geometry depends on a true-north reference because
        // the chart is true-north Mercator.
        var nav = new NavigationData { PreferMagneticHeading = true };
        nav.Apply("navigation.headingTrue", JsonSerializer.SerializeToElement(3.27));
        nav.Apply("navigation.headingMagnetic", JsonSerializer.SerializeToElement(3.40));
        await Assert.That(nav.HeadingTrueResolved).IsEqualTo(3.27);
    }

    [Test]
    public async Task HeadingTrueResolved_FallsBackToMagneticWhenTrueAbsent()
    {
        // Boat publishes only magnetic. Geometry consumers (radar
        // overlay) need *something*; magnetic-without-variation
        // rotates by the local variation but that beats "stuck
        // pointing north" until the helm fixes their SK config.
        var nav = new NavigationData();
        nav.Apply("navigation.headingMagnetic", JsonSerializer.SerializeToElement(3.40));
        await Assert.That(nav.HeadingTrueResolved).IsEqualTo(3.40);
    }

    [Test]
    public async Task HeadingTrueResolved_NullWhenNeitherPublished()
    {
        // No heading at all: null lets consumers fall through to their
        // own fallbacks (COG, then 0). The marker / radar overlay
        // shouldn't lock to a stale value just because the radar
        // wants something to rotate by.
        var nav = new NavigationData();
        await Assert.That(nav.HeadingTrueResolved).IsNull();
    }

    [Test]
    public async Task Apply_DesignDraftCurrent_SetsDraftFromSignalK()
    {
        // SignalK's design.draft.current is the "as loaded" draft,
        // preferred over .maximum. Anchor-tide alarm uses this auto.
        var nav = new NavigationData();
        nav.Apply("design.draft.current", JsonSerializer.SerializeToElement(1.85));
        await Assert.That(nav.DraftFromSignalK).IsEqualTo(1.85);
    }

    [Test]
    public async Task Apply_DesignDraftMaximum_UsedAsFallback()
    {
        // Some servers publish only .maximum. We take it.
        var nav = new NavigationData();
        nav.Apply("design.draft.maximum", JsonSerializer.SerializeToElement(2.10));
        await Assert.That(nav.DraftFromSignalK).IsEqualTo(2.10);
    }

    [Test]
    public async Task Apply_DesignDraftCurrent_BeatsMaximum()
    {
        // When both are published, .current wins regardless of order.
        // Guards against a race where an early .maximum delta would
        // otherwise stick even after .current arrived.
        var nav = new NavigationData();
        nav.Apply("design.draft.maximum", JsonSerializer.SerializeToElement(2.10));
        nav.Apply("design.draft.current", JsonSerializer.SerializeToElement(1.85));
        await Assert.That(nav.DraftFromSignalK).IsEqualTo(1.85);

        // And if .current shows up first, a later .maximum shouldn't
        // clobber it.
        var nav2 = new NavigationData();
        nav2.Apply("design.draft.current", JsonSerializer.SerializeToElement(1.85));
        nav2.Apply("design.draft.maximum", JsonSerializer.SerializeToElement(2.10));
        await Assert.That(nav2.DraftFromSignalK).IsEqualTo(1.85);
    }

    [Test]
    public async Task Apply_SpeedOverGround_SetsProperty()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(5.14);
        nav.Apply("navigation.speedOverGround", je);
        await Assert.That(nav.SpeedOverGround).IsEqualTo(5.14);
    }

    [Test]
    public async Task Apply_NullValue_ReturnsFalse()
    {
        var nav = new NavigationData();
        await Assert.That(nav.Apply("navigation.speedOverGround", null)).IsFalse();
        await Assert.That(nav.SpeedOverGround).IsNull();
    }

    [Test]
    public async Task Apply_StringValue_ReturnsFalse()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement("not a number");
        await Assert.That(nav.Apply("navigation.speedOverGround", je)).IsFalse();
    }

    [Test]
    public async Task ApplyPosition_SetsLatLon()
    {
        var nav = new NavigationData();
        nav.ApplyPosition(47.39, 8.54);
        await Assert.That(nav.Latitude).IsEqualTo(47.39);
        await Assert.That(nav.Longitude).IsEqualTo(8.54);
    }

    [Test]
    public async Task Apply_NavigationPosition_ReturnsFalse_HandledSeparately()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        await Assert.That(nav.Apply("navigation.position", je)).IsFalse();
    }

    // --- Anchor properties ---

    [Test]
    public async Task AnchorActive_FalseByDefault()
    {
        var nav = new NavigationData();
        await Assert.That(nav.AnchorActive).IsFalse();
    }

    [Test]
    public async Task ApplyAnchorPosition_SetsAnchorActiveTrue()
    {
        var nav = new NavigationData();
        nav.ApplyAnchorPosition(47.39, 8.54);
        await Assert.That(nav.AnchorActive).IsTrue();
        await Assert.That(nav.AnchorLatitude).IsEqualTo(47.39);
        await Assert.That(nav.AnchorLongitude).IsEqualTo(8.54);
    }

    [Test]
    public async Task ClearAnchor_ResetsAllAnchorProperties()
    {
        var nav = new NavigationData();
        nav.ApplyAnchorPosition(47.39, 8.54);
        nav.Apply("navigation.anchor.maxRadius", JsonSerializer.SerializeToElement(30.0));
        nav.Apply("navigation.anchor.currentRadius", JsonSerializer.SerializeToElement(12.5));
        // Set every v2.0.0+ field too so we can pin that ClearAnchor
        // resets them; otherwise a raise+re-drop in a different
        // anchorage would surface stale rode / bearing for one tick.
        nav.Apply("navigation.anchor.bearingTrue", JsonSerializer.SerializeToElement(1.5));
        nav.Apply("navigation.anchor.apparentBearing", JsonSerializer.SerializeToElement(0.7));
        nav.Apply("navigation.anchor.rodeLength", JsonSerializer.SerializeToElement(60.0));
        nav.Apply("navigation.anchor.distanceFromBow", JsonSerializer.SerializeToElement(18.0));

        nav.ClearAnchor();

        await Assert.That(nav.AnchorActive).IsFalse();
        await Assert.That(nav.AnchorLatitude).IsNull();
        await Assert.That(nav.AnchorLongitude).IsNull();
        await Assert.That(nav.AnchorMaxRadius).IsNull();
        await Assert.That(nav.AnchorCurrentRadius).IsNull();
        // Peak resets too - the next anchor drop must not greet the
        // helm with yesterday's worst-case distance.
        await Assert.That(nav.AnchorPeakRadius).IsNull();
        // v2.0.0+ plugin-published fields all clear so a raise +
        // re-drop in a different anchorage doesn't show stale rode /
        // bearing for one tick before the new ones land.
        await Assert.That(nav.AnchorBearingTrue).IsNull();
        await Assert.That(nav.AnchorApparentBearing).IsNull();
        await Assert.That(nav.AnchorRodeLength).IsNull();
        await Assert.That(nav.AnchorDistanceFromBow).IsNull();
    }

    [Test]
    public async Task Apply_AnchorBearingTrue_SetsProperty()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(1.234);
        await Assert.That(nav.Apply("navigation.anchor.bearingTrue", je)).IsTrue();
        await Assert.That(nav.AnchorBearingTrue).IsEqualTo(1.234);
    }

    [Test]
    public async Task Apply_AnchorApparentBearing_SetsProperty()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(0.78);
        await Assert.That(nav.Apply("navigation.anchor.apparentBearing", je)).IsTrue();
        await Assert.That(nav.AnchorApparentBearing).IsEqualTo(0.78);
    }

    [Test]
    public async Task Apply_AnchorRodeLength_SetsProperty()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(45.5);
        await Assert.That(nav.Apply("navigation.anchor.rodeLength", je)).IsTrue();
        await Assert.That(nav.AnchorRodeLength).IsEqualTo(45.5);
    }

    [Test]
    public async Task Apply_AnchorDistanceFromBow_SetsProperty()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(17.2);
        await Assert.That(nav.Apply("navigation.anchor.distanceFromBow", je)).IsTrue();
        await Assert.That(nav.AnchorDistanceFromBow).IsEqualTo(17.2);
    }

    [Test]
    public async Task Apply_AnchorMaxRadius_SetsProperty()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(25.0);
        await Assert.That(nav.Apply("navigation.anchor.maxRadius", je)).IsTrue();
        await Assert.That(nav.AnchorMaxRadius).IsEqualTo(25.0);
    }

    [Test]
    public async Task Apply_AnchorCurrentRadius_SetsProperty()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(18.3);
        await Assert.That(nav.Apply("navigation.anchor.currentRadius", je)).IsTrue();
        await Assert.That(nav.AnchorCurrentRadius).IsEqualTo(18.3);
    }

    [Test]
    public async Task Apply_AnchorCurrentRadius_TracksPeakOnFirstWrite()
    {
        var nav = new NavigationData();
        nav.Apply("navigation.anchor.currentRadius", JsonSerializer.SerializeToElement(12.5));
        await Assert.That(nav.AnchorPeakRadius).IsEqualTo(12.5);
    }

    [Test]
    public async Task Apply_AnchorCurrentRadius_PeakBumpsOnHigherValue()
    {
        var nav = new NavigationData();
        nav.Apply("navigation.anchor.currentRadius", JsonSerializer.SerializeToElement(12.5));
        nav.Apply("navigation.anchor.currentRadius", JsonSerializer.SerializeToElement(18.0));
        await Assert.That(nav.AnchorCurrentRadius).IsEqualTo(18.0);
        await Assert.That(nav.AnchorPeakRadius).IsEqualTo(18.0);
    }

    [Test]
    public async Task Apply_AnchorCurrentRadius_PeakHoldsOnLowerValue()
    {
        // Boat swings closer to the anchor: live value drops, but the
        // peak must hold so the helm can still see "we drifted to N m
        // at the worst" once the boat is back inside the alarm circle.
        var nav = new NavigationData();
        nav.Apply("navigation.anchor.currentRadius", JsonSerializer.SerializeToElement(25.0));
        nav.Apply("navigation.anchor.currentRadius", JsonSerializer.SerializeToElement(18.0));
        await Assert.That(nav.AnchorCurrentRadius).IsEqualTo(18.0);
        await Assert.That(nav.AnchorPeakRadius).IsEqualTo(25.0);
    }

    [Test]
    public async Task Apply_AnchorCurrentRadius_PeakResetsOnClearAnchor()
    {
        var nav = new NavigationData();
        nav.Apply("navigation.anchor.currentRadius", JsonSerializer.SerializeToElement(40.0));
        nav.ClearAnchor();
        // After ClearAnchor the next anchor drop must start from a
        // clean slate, otherwise yesterday's drift would shadow the
        // current watch.
        nav.ApplyAnchorPosition(47.39, 8.54);
        nav.Apply("navigation.anchor.currentRadius", JsonSerializer.SerializeToElement(5.0));
        await Assert.That(nav.AnchorPeakRadius).IsEqualTo(5.0);
    }

    [Test]
    public async Task Apply_AnchorCurrentRadius_PeakResetsOnReDropWithoutClear()
    {
        // Some plugins / workflows re-drop without nulling the previous
        // anchor position first ("move anchor", plugin upgrade, two
        // clients racing the drop). Without the in-position-change
        // reset the previous-spot peak persists into the new location
        // and the HUD shows yesterday's high-water mark on tonight's
        // arrival.
        var nav = new NavigationData();
        nav.ApplyAnchorPosition(47.39, 8.54);
        nav.Apply("navigation.anchor.currentRadius", JsonSerializer.SerializeToElement(35.0));
        await Assert.That(nav.AnchorPeakRadius).IsEqualTo(35.0);
        // Re-drop a few hundred metres away without ClearAnchor first.
        nav.ApplyAnchorPosition(47.395, 8.545);
        nav.Apply("navigation.anchor.currentRadius", JsonSerializer.SerializeToElement(5.0));
        // Peak should now reflect only the new anchorage; the 35 m
        // figure belonged to the previous spot.
        await Assert.That(nav.AnchorPeakRadius).IsEqualTo(5.0);
    }

    [Test]
    public async Task Apply_AnchorCurrentRadius_PeakUnchangedOnIdenticalPositionUpdate()
    {
        // Plugins that re-broadcast the same anchor.position every
        // delta tick must not reset the peak. The 1 m floor in
        // ApplyAnchorPosition is what protects against that. Mirror
        // a typical "still at the same drop" scenario.
        var nav = new NavigationData();
        nav.ApplyAnchorPosition(47.39, 8.54);
        nav.Apply("navigation.anchor.currentRadius", JsonSerializer.SerializeToElement(28.0));
        await Assert.That(nav.AnchorPeakRadius).IsEqualTo(28.0);
        // GPS jitter on the anchor position itself: same metric coords,
        // sub-millimetre lat/lon delta.
        nav.ApplyAnchorPosition(47.390000005, 8.540000005);
        nav.Apply("navigation.anchor.currentRadius", JsonSerializer.SerializeToElement(12.0));
        await Assert.That(nav.AnchorPeakRadius).IsEqualTo(28.0);
    }

    // --- Course properties ---

    [Test]
    public async Task HasActiveCourse_FalseByDefault()
    {
        var nav = new NavigationData();
        await Assert.That(nav.HasActiveCourse).IsFalse();
    }

    [Test]
    public async Task ApplyCourseNextPointPosition_SetsActiveCourseTrue()
    {
        var nav = new NavigationData();
        nav.ApplyCourseNextPointPosition(48.0, 9.0);
        await Assert.That(nav.HasActiveCourse).IsTrue();
        await Assert.That(nav.CourseNextPointLatitude).IsEqualTo(48.0);
        await Assert.That(nav.CourseNextPointLongitude).IsEqualTo(9.0);
    }

    [Test]
    public async Task Apply_CourseDistance_SetsProperty()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(5000.0);
        await Assert.That(nav.Apply("navigation.course.calcValues.distance", je)).IsTrue();
        await Assert.That(nav.CourseNextPointDistance).IsEqualTo(5000.0);
    }

    [Test]
    public async Task Apply_ArrivalCircle_SetsProperty()
    {
        // navigation.course.arrivalCircle (server-published per-leg
        // arrival radius in metres). Drives the chart ring + APPROACH
        // alarm threshold so they agree with arrivalCircleEntered.
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(75.0);
        await Assert.That(nav.Apply("navigation.course.arrivalCircle", je)).IsTrue();
        await Assert.That(nav.CourseArrivalCircleMeters).IsEqualTo(75.0);
    }

    [Test]
    public async Task ClearCourse_NullsArrivalCircle()
    {
        // Route-clear must drop the cached radius so a later route
        // activation that doesn't carry arrivalCircle (minimal SK
        // server) doesn't inherit the old leg's value.
        var nav = new NavigationData();
        nav.Apply("navigation.course.arrivalCircle",
            JsonSerializer.SerializeToElement(75.0));
        await Assert.That(nav.CourseArrivalCircleMeters).IsEqualTo(75.0);

        nav.ClearCourse();

        await Assert.That(nav.CourseArrivalCircleMeters).IsNull();
    }

    [Test]
    public async Task Apply_CourseBearing_SetsProperty()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(1.57);
        await Assert.That(nav.Apply("navigation.course.calcValues.bearingTrue", je)).IsTrue();
        await Assert.That(nav.CourseNextPointBearing).IsEqualTo(1.57);
    }

    [Test]
    public async Task Apply_CourseVmg_SetsProperty_FromVelocityMadeGood()
    {
        // course-provider-plugin publishes velocityMadeGood (null when
        // VMG isn't meaningful on a motor leg). The earlier hypothetical
        // velocityMadeGoodToCourse fallback was unsupported by the
        // upstream plugin and has been dropped.
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(3.45);
        await Assert.That(nav.Apply("navigation.course.calcValues.velocityMadeGood", je)).IsTrue();
        await Assert.That(nav.CourseNextPointVmg).IsEqualTo(3.45);
    }

    [Test]
    public async Task Apply_VelocityMadeGoodToCourse_NotRecognized()
    {
        // Pin the absence: nothing in the SignalK Course API spec or
        // the course-provider plugin emits velocityMadeGoodToCourse.
        // Apply must return false so a future re-add to NavigationData
        // shows up here before it ships.
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(3.45);
        await Assert.That(nav.Apply("navigation.course.calcValues.velocityMadeGoodToCourse", je)).IsFalse();
    }

    [Test]
    public async Task Apply_RouteDistanceRemaining_SetsProperty()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(12345.6);
        await Assert.That(nav.Apply("navigation.course.calcValues.route.distance", je)).IsTrue();
        await Assert.That(nav.ActiveRouteDistanceRemaining).IsEqualTo(12345.6);
    }

    [Test]
    public async Task ApplyString_ActiveRouteHref_SetsProperty()
    {
        var nav = new NavigationData();
        await Assert.That(nav.ApplyString("navigation.course.activeRoute.href", "/resources/routes/abc")).IsTrue();
        await Assert.That(nav.ActiveRouteHref).IsEqualTo("/resources/routes/abc");
    }

    [Test]
    public async Task Apply_CourseTimeToGo_SetsProperty()
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(3600.0);
        await Assert.That(nav.Apply("navigation.course.calcValues.timeToGo", je)).IsTrue();
        await Assert.That(nav.CourseNextPointTimeToGo).IsEqualTo(3600.0);
    }

    [Test]
    public async Task ApplyString_LegacyV1Paths_AreRejected()
    {
        // Post-tech-debt sweep: the v1 courseGreatCircle /
        // courseRhumbline names are no longer handled. ApplyString
        // must return false so a server still emitting them doesn't
        // silently set ActiveRouteHref.
        var nav = new NavigationData();
        await Assert.That(nav.ApplyString("navigation.courseGreatCircle.activeRoute.href", "x")).IsFalse();
        await Assert.That(nav.ApplyString("navigation.courseRhumbline.activeRoute.href", "x")).IsFalse();
        await Assert.That(nav.ApplyString("navigation.courseGreatCircle.activeRoute.name", "x")).IsFalse();
    }

    [Test]
    public async Task Apply_LegacyV1Paths_AreRejected()
    {
        // Matching pin for Apply(): number-valued v1 course paths
        // no longer match a case.
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(1.0);
        await Assert.That(nav.Apply("navigation.courseGreatCircle.nextPoint.distance", je)).IsFalse();
        await Assert.That(nav.Apply("navigation.courseRhumbline.activeRoute.distanceRemaining", je)).IsFalse();
    }

    [Test]
    public async Task ApplyString_UnknownPath_ReturnsFalse()
    {
        var nav = new NavigationData();
        await Assert.That(nav.ApplyString("some.unknown", "value")).IsFalse();
    }

    // --- Timestamp ---

    [Test]
    public async Task MarkDataReceived_SetsLastReceivedUtcFromInjectedClock()
    {
        var fixedNow = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var nav = new NavigationData(() => fixedNow);
        nav.MarkDataReceived();
        await Assert.That(nav.LastReceivedUtc).IsEqualTo(fixedNow);
    }

    // --- Tide (from signalk-tides-api / mxtide / similar) ---

    [Test]
    public async Task Apply_TideHeights_SetsProperties()
    {
        var nav = new NavigationData();
        nav.Apply("environment.tide.heightNow", JsonSerializer.SerializeToElement(1.8));
        nav.Apply("environment.tide.heightHigh", JsonSerializer.SerializeToElement(3.2));
        nav.Apply("environment.tide.heightLow", JsonSerializer.SerializeToElement(0.4));

        await Assert.That(nav.TideHeightNow).IsEqualTo(1.8);
        await Assert.That(nav.TideHeightHigh).IsEqualTo(3.2);
        await Assert.That(nav.TideHeightLow).IsEqualTo(0.4);
    }

    [Test]
    public async Task ApplyString_TideTimes_ParsedAsUtc()
    {
        // Tide-plugin timestamps are ISO 8601. Accept both explicit-UTC
        // and unqualified forms; the model must pin Kind=Utc either way
        // so downstream comparisons against DateTime.UtcNow are sound.
        var nav = new NavigationData();
        nav.ApplyString("environment.tide.timeHigh", "2026-04-18T14:32:00Z");
        nav.ApplyString("environment.tide.timeLow", "2026-04-18T20:48:00");

        await Assert.That(nav.TideTimeHigh).IsNotNull();
        await Assert.That(nav.TideTimeHigh!.Value.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert.That(nav.TideTimeHigh.Value.Hour).IsEqualTo(14);
        await Assert.That(nav.TideTimeHigh.Value.Minute).IsEqualTo(32);

        await Assert.That(nav.TideTimeLow).IsNotNull();
        await Assert.That(nav.TideTimeLow!.Value.Kind).IsEqualTo(DateTimeKind.Utc);
    }

    [Test]
    public async Task ApplyString_TideTime_InvalidString_LeavesNull()
    {
        var nav = new NavigationData();
        // Malformed timestamp should be swallowed (no exception bubbling
        // up to crash the delta-receive loop) and the property stays null.
        nav.ApplyString("environment.tide.timeHigh", "not-a-date");
        await Assert.That(nav.TideTimeHigh).IsNull();
    }

    [Test]
    public async Task ApplyString_TideStationName_SetsProperty()
    {
        var nav = new NavigationData();
        nav.ApplyString("environment.tide.stationName", "Lerwick");
        await Assert.That(nav.TideStationName).IsEqualTo("Lerwick");
    }

    // --- Previous course point + HasPreviousPoint ---

    [Test]
    public async Task HasPreviousPoint_FalseByDefault()
    {
        // Boundary: a fresh model must report no previous point so the
        // route renderer doesn't try to draw a passed-leg segment from
        // garbage state on first paint.
        var nav = new NavigationData();
        await Assert.That(nav.HasPreviousPoint).IsFalse();
    }

    [Test]
    public async Task ApplyCoursePreviousPointPosition_SetsHasPreviousPointTrue()
    {
        // The course-provider plugin publishes the previous waypoint
        // during multi-leg routing; the renderer needs it to draw the
        // active-leg line FROM the previous WP TO the next WP. Without
        // this pair the line would either be missing or drawn from the
        // boat (wrong - that's the boat-to-WP heading, not the leg).
        var nav = new NavigationData();
        nav.ApplyCoursePreviousPointPosition(47.0, 8.5);
        await Assert.That(nav.HasPreviousPoint).IsTrue();
        await Assert.That(nav.CoursePreviousPointLatitude).IsEqualTo(47.0);
        await Assert.That(nav.CoursePreviousPointLongitude).IsEqualTo(8.5);
    }

    [Test]
    public async Task ClearCourse_ClearsPreviousPoint()
    {
        // After a route deactivation, every course-related field has to
        // reset together. A leftover previous point would render an
        // active-leg artifact when no route is active.
        var nav = new NavigationData();
        nav.ApplyCoursePreviousPointPosition(47.0, 8.5);
        nav.ClearCourse();
        await Assert.That(nav.HasPreviousPoint).IsFalse();
        await Assert.That(nav.CoursePreviousPointLatitude).IsNull();
        await Assert.That(nav.CoursePreviousPointLongitude).IsNull();
    }

    // --- Autopilot, rudder, current ---

    [Test]
    public async Task ApplyString_AutopilotState_SetsProperty()
    {
        // Autopilot state strings ("standby", "auto", "route", "wind")
        // drive HUD chip rendering; the value goes through verbatim.
        var nav = new NavigationData();
        await Assert.That(nav.ApplyString("steering.autopilot.state", "auto")).IsTrue();
        await Assert.That(nav.AutopilotState).IsEqualTo("auto");
    }

    [Test]
    public async Task Apply_AutopilotTargetWindAngle_SetsProperty()
    {
        // Wind-mode AP target. Used by the HDG HUD to display "tracking
        // wind at ~Xdeg" so the helm can see the AP's intended hold
        // alongside the current AWA.
        var nav = new NavigationData();
        nav.Apply("steering.autopilot.target.windAngleApparent", 0.78);
        await Assert.That(nav.AutopilotTargetWindAngle).IsEqualTo(0.78);
    }

    [Test]
    public async Task Apply_RudderAngle_PreferredOverAutopilotFallback()
    {
        // SignalK spec path is steering.rudderAngle. Some AP plugins
        // also publish steering.autopilot.rudderAngle; the spec path
        // wins when both are present so a server publishing both
        // doesn't flap between them. Pin both orderings.
        var nav1 = new NavigationData();
        nav1.Apply("steering.autopilot.rudderAngle", 0.05);
        nav1.Apply("steering.rudderAngle", 0.10);
        await Assert.That(nav1.RudderAngle).IsEqualTo(0.10);

        var nav2 = new NavigationData();
        nav2.Apply("steering.rudderAngle", 0.10);
        nav2.Apply("steering.autopilot.rudderAngle", 0.05);
        await Assert.That(nav2.RudderAngle).IsEqualTo(0.10);
    }

    [Test]
    public async Task Apply_AutopilotRudderAngle_UsedAsFallback_WhenPrimaryAbsent()
    {
        // Some AP plugins only publish under steering.autopilot.rudderAngle.
        // Without the fallback, the HDG HUD's "AP rudder" line would
        // stay blank and the helm couldn't see how hard the AP is
        // working to hold course.
        var nav = new NavigationData();
        nav.Apply("steering.autopilot.rudderAngle", -0.12);
        await Assert.That(nav.RudderAngle).IsEqualTo(-0.12);
    }

    [Test]
    public async Task Apply_CurrentSetAndDrift_SetsProperties()
    {
        // Tidal current data drives leeway / set arrows on the chart.
        // Both fields must round-trip independently.
        var nav = new NavigationData();
        nav.Apply("environment.current.setTrue", 1.57);   // east
        nav.Apply("environment.current.drift", 0.5);      // m/s
        await Assert.That(nav.CurrentSet).IsEqualTo(1.57);
        await Assert.That(nav.CurrentDrift).IsEqualTo(0.5);
    }

    [Test]
    public async Task Apply_CrossTrackError_SetsProperty()
    {
        // XTE drives the route deviation chip; pin the path so a SignalK
        // schema rename (calcValues vs courseGreatCircle vs ...) shows up
        // here as a failure rather than a silent dashboard regression.
        var nav = new NavigationData();
        nav.Apply("navigation.course.calcValues.crossTrackError", -25.5);
        await Assert.That(nav.CrossTrackError).IsEqualTo(-25.5);
    }

    [Test]
    public async Task Apply_RoutePointIndexTotal_RoundTripsThroughInt()
    {
        // SignalK delivers pointIndex / pointTotal as numbers. The model
        // casts to int at the boundary so NaN / fractional payloads from
        // a misbehaving server can't poison the HUD's "WP 3 of 7" line.
        // Pin the int conversion explicitly.
        var nav = new NavigationData();
        nav.Apply("navigation.course.activeRoute.pointIndex", 2.0);
        nav.Apply("navigation.course.activeRoute.pointTotal", 7.0);
        await Assert.That(nav.ActiveRoutePointIndex).IsEqualTo(2);
        await Assert.That(nav.ActiveRoutePointTotal).IsEqualTo(7);
    }

    [Test]
    public async Task Apply_RouteTimeToGo_SetsProperty()
    {
        // Per-route ETA chip on the HUD. Distinct from CourseNextPointTimeToGo.
        var nav = new NavigationData();
        nav.Apply("navigation.course.calcValues.route.timeToGo", 7200.0);
        await Assert.That(nav.ActiveRouteTimeToGo).IsEqualTo(7200.0);
    }

    // --- ApplyBool: arrival circle / perpendicular passed flags ---

    [Test]
    [Arguments("notifications.navigation.course.perpendicularPassed")]
    [Arguments("navigation.course.perpendicularPassed")]
    [Arguments("navigation.course.calcValues.perpendicularPassed")]
    public async Task ApplyBool_PerpendicularPassed_AcceptsAllThreePaths(string path)
    {
        // Three flavours seen in the wild across plugin versions. All
        // three must land on PerpendicularPassed so an upstream plugin
        // upgrade that switches paths doesn't silently break the
        // auto-advance trigger.
        var nav = new NavigationData();
        await Assert.That(nav.ApplyBool(path, true)).IsTrue();
        await Assert.That(nav.PerpendicularPassed).IsTrue();
    }

    [Test]
    [Arguments("notifications.navigation.course.arrivalCircleEntered")]
    [Arguments("navigation.course.arrivalCircleEntered")]
    [Arguments("navigation.course.calcValues.arrivalCircleEntered")]
    public async Task ApplyBool_ArrivalCircleEntered_AcceptsAllThreePaths(string path)
    {
        // Same three-way path acceptance as perpendicularPassed; same
        // rationale (compatibility with course-provider plugin variants).
        var nav = new NavigationData();
        await Assert.That(nav.ApplyBool(path, true)).IsTrue();
        await Assert.That(nav.ArrivalCircleEntered).IsTrue();
    }

    [Test]
    public async Task ApplyBool_UnknownPath_ReturnsFalse()
    {
        var nav = new NavigationData();
        await Assert.That(nav.ApplyBool("some.unknown.bool.path", true)).IsFalse();
    }

    [Test]
    public async Task ClearCourse_ResetsPerpendicularAndArrivalFlags()
    {
        // Stale auto-advance flags surviving across route changes would
        // cause a one-tick spurious advance the moment a new route
        // activates - the same bug that the original auto-advance fix
        // had to fight. Pin the reset.
        var nav = new NavigationData();
        nav.ApplyBool("navigation.course.calcValues.perpendicularPassed", true);
        nav.ApplyBool("navigation.course.calcValues.arrivalCircleEntered", true);
        nav.ClearCourse();
        await Assert.That(nav.PerpendicularPassed).IsNull();
        await Assert.That(nav.ArrivalCircleEntered).IsNull();
    }

    // --- ApplyString: solar state ---

    [Test]
    [Arguments("day", "day")]
    [Arguments("DAY", "day")]                   // case normalised
    [Arguments("  Night  ", "night")]           // trim + lower
    [Arguments("dawn", "dawn")]
    [Arguments("dusk", "dusk")]
    public async Task ApplyString_SunState_NormalisesToLowerInvariant(string raw, string expected)
    {
        // Some solar plugins emit "Day", others "DAY", others "  day ".
        // Lower-cased + trimmed normalisation lets downstream
        // string-equality checks (auto-night detection) work without
        // care for case drift between plugin builds.
        var nav = new NavigationData();
        await Assert.That(nav.ApplyString("environment.sun", raw)).IsTrue();
        await Assert.That(nav.SunState).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task ApplyString_SunState_BlankBecomesNull(string? raw)
    {
        // Blank / whitespace-only values shouldn't pollute SunState
        // with empty string, which would make downstream "is it night?"
        // checks falsely match against "". null is the canonical
        // "not published" marker.
        var nav = new NavigationData();
        nav.ApplyString("environment.sun", raw);
        await Assert.That(nav.SunState).IsNull();
    }

    // --- ConvertToDouble: float + long ingest paths ---

    [Test]
    public async Task Apply_LongValue_AcceptedAsDouble()
    {
        // Some servers serialise integers as JSON longs (large radar
        // ranges, MMSI-style numbers). The model's ConvertToDouble
        // helper must accept long without falling through to "unknown".
        var nav = new NavigationData();
        await Assert.That(nav.Apply("navigation.speedOverGround", 5L)).IsTrue();
        await Assert.That(nav.SpeedOverGround).IsEqualTo(5.0);
    }

    [Test]
    public async Task Apply_FloatValue_AcceptedAsDouble()
    {
        // Float ingest matters for embedded NMEA gateways that send
        // single-precision numbers; the boxing path through Apply
        // must not silently drop them.
        var nav = new NavigationData();
        await Assert.That(nav.Apply("navigation.speedOverGround", 5.25f)).IsTrue();
        await Assert.That(nav.SpeedOverGround).IsEqualTo(5.25);
    }

    [Test]
    public async Task Apply_IntValue_AcceptedAsDouble()
    {
        var nav = new NavigationData();
        await Assert.That(nav.Apply("navigation.speedOverGround", 5)).IsTrue();
        await Assert.That(nav.SpeedOverGround).IsEqualTo(5.0);
    }
}
