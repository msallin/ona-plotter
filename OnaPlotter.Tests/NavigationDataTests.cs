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

        nav.ClearAnchor();

        await Assert.That(nav.AnchorActive).IsFalse();
        await Assert.That(nav.AnchorLatitude).IsNull();
        await Assert.That(nav.AnchorLongitude).IsNull();
        await Assert.That(nav.AnchorMaxRadius).IsNull();
        await Assert.That(nav.AnchorCurrentRadius).IsNull();
        // Peak resets too -- the next anchor drop must not greet the
        // helm with yesterday's worst-case distance.
        await Assert.That(nav.AnchorPeakRadius).IsNull();
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
}
