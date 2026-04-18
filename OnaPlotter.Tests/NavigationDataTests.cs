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
    [Arguments("navigation.courseGreatCircle.nextPoint.distance")]
    [Arguments("navigation.courseRhumbline.nextPoint.distance")]
    public async Task Apply_CourseDistance_SetsProperty(string path)
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(5000.0);
        await Assert.That(nav.Apply(path, je)).IsTrue();
        await Assert.That(nav.CourseNextPointDistance).IsEqualTo(5000.0);
    }

    [Test]
    [Arguments("navigation.courseGreatCircle.nextPoint.bearingTrue")]
    [Arguments("navigation.courseRhumbline.nextPoint.bearingTrue")]
    public async Task Apply_CourseBearing_SetsProperty(string path)
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(1.57);
        await Assert.That(nav.Apply(path, je)).IsTrue();
        await Assert.That(nav.CourseNextPointBearing).IsEqualTo(1.57);
    }

    [Test]
    [Arguments("navigation.courseGreatCircle.nextPoint.timeToGo")]
    [Arguments("navigation.courseRhumbline.nextPoint.timeToGo")]
    public async Task Apply_CourseTimeToGo_SetsProperty(string path)
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(3600.0);
        await Assert.That(nav.Apply(path, je)).IsTrue();
        await Assert.That(nav.CourseNextPointTimeToGo).IsEqualTo(3600.0);
    }

    [Test]
    [Arguments("navigation.courseGreatCircle.nextPoint.velocityMadeGood")]
    [Arguments("navigation.courseRhumbline.nextPoint.velocityMadeGood")]
    public async Task Apply_CourseVmg_SetsProperty(string path)
    {
        var nav = new NavigationData();
        var je = JsonSerializer.SerializeToElement(2.8);
        await Assert.That(nav.Apply(path, je)).IsTrue();
        await Assert.That(nav.CourseNextPointVmg).IsEqualTo(2.8);
    }

    // --- ApplyString ---

    [Test]
    [Arguments("navigation.courseGreatCircle.activeRoute.href", "/resources/routes/abc")]
    [Arguments("navigation.courseRhumbline.activeRoute.href", "/resources/routes/xyz")]
    public async Task ApplyString_RouteHref_SetsProperty(string path, string value)
    {
        var nav = new NavigationData();
        await Assert.That(nav.ApplyString(path, value)).IsTrue();
        await Assert.That(nav.ActiveRouteHref).IsEqualTo(value);
    }

    [Test]
    [Arguments("navigation.courseGreatCircle.activeRoute.name", "To harbor")]
    [Arguments("navigation.courseRhumbline.activeRoute.name", "Sunday sail")]
    public async Task ApplyString_RouteName_SetsProperty(string path, string value)
    {
        var nav = new NavigationData();
        await Assert.That(nav.ApplyString(path, value)).IsTrue();
        await Assert.That(nav.ActiveRouteName).IsEqualTo(value);
    }

    [Test]
    public async Task ApplyString_UnknownPath_ReturnsFalse()
    {
        var nav = new NavigationData();
        await Assert.That(nav.ApplyString("some.unknown", "value")).IsFalse();
    }

    // --- Timestamp ---

    [Test]
    public async Task SetTimestamp_SetsProperty()
    {
        var nav = new NavigationData();
        nav.SetTimestamp("2025-01-01T00:00:00Z");
        await Assert.That(nav.LastTimestamp).IsEqualTo("2025-01-01T00:00:00Z");
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
