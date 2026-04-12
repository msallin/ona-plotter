using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Tests;

public class NavigationDataTests
{
    private readonly NavigationData _nav = new();

    [Theory]
    [InlineData("navigation.speedOverGround", 3.5)]
    [InlineData("navigation.courseOverGroundTrue", 1.2)]
    [InlineData("navigation.headingTrue", 0.78)]
    [InlineData("environment.depth.belowTransducer", 12.4)]
    [InlineData("environment.wind.angleApparent", -0.5)]
    [InlineData("environment.wind.speedApparent", 7.2)]
    public void Apply_RecognizedPath_ReturnsTrue(string path, double value)
    {
        var je = JsonSerializer.SerializeToElement(value);
        Assert.True(_nav.Apply(path, je));
    }

    [Fact]
    public void Apply_UnknownPath_ReturnsFalse()
    {
        var je = JsonSerializer.SerializeToElement(1.0);
        Assert.False(_nav.Apply("some.unknown.path", je));
    }

    [Fact]
    public void Apply_SpeedOverGround_SetsProperty()
    {
        var je = JsonSerializer.SerializeToElement(5.14);
        _nav.Apply("navigation.speedOverGround", je);
        Assert.Equal(5.14, _nav.SpeedOverGround);
    }

    [Fact]
    public void Apply_NullValue_ReturnsFalse()
    {
        Assert.False(_nav.Apply("navigation.speedOverGround", null));
        Assert.Null(_nav.SpeedOverGround);
    }

    [Fact]
    public void Apply_StringValue_ReturnsFalse()
    {
        var je = JsonSerializer.SerializeToElement("not a number");
        Assert.False(_nav.Apply("navigation.speedOverGround", je));
    }

    [Fact]
    public void ApplyPosition_SetsLatLon()
    {
        _nav.ApplyPosition(47.39, 8.54);
        Assert.Equal(47.39, _nav.Latitude);
        Assert.Equal(8.54, _nav.Longitude);
    }

    [Fact]
    public void Apply_NavigationPosition_ReturnsFalse_HandledSeparately()
    {
        var je = JsonSerializer.SerializeToElement(new { latitude = 47.0, longitude = 8.0 });
        Assert.False(_nav.Apply("navigation.position", je));
    }

    // --- Anchor properties ---

    [Fact]
    public void AnchorActive_FalseByDefault()
    {
        Assert.False(_nav.AnchorActive);
    }

    [Fact]
    public void ApplyAnchorPosition_SetsAnchorActiveTrue()
    {
        _nav.ApplyAnchorPosition(47.39, 8.54);
        Assert.True(_nav.AnchorActive);
        Assert.Equal(47.39, _nav.AnchorLatitude);
        Assert.Equal(8.54, _nav.AnchorLongitude);
    }

    [Fact]
    public void ClearAnchor_ResetsAllAnchorProperties()
    {
        _nav.ApplyAnchorPosition(47.39, 8.54);
        var je = JsonSerializer.SerializeToElement(30.0);
        _nav.Apply("navigation.anchor.maxRadius", je);
        var je2 = JsonSerializer.SerializeToElement(12.5);
        _nav.Apply("navigation.anchor.currentRadius", je2);

        _nav.ClearAnchor();

        Assert.False(_nav.AnchorActive);
        Assert.Null(_nav.AnchorLatitude);
        Assert.Null(_nav.AnchorLongitude);
        Assert.Null(_nav.AnchorMaxRadius);
        Assert.Null(_nav.AnchorCurrentRadius);
    }

    [Fact]
    public void Apply_AnchorMaxRadius_SetsProperty()
    {
        var je = JsonSerializer.SerializeToElement(25.0);
        Assert.True(_nav.Apply("navigation.anchor.maxRadius", je));
        Assert.Equal(25.0, _nav.AnchorMaxRadius);
    }

    [Fact]
    public void Apply_AnchorCurrentRadius_SetsProperty()
    {
        var je = JsonSerializer.SerializeToElement(18.3);
        Assert.True(_nav.Apply("navigation.anchor.currentRadius", je));
        Assert.Equal(18.3, _nav.AnchorCurrentRadius);
    }

    // --- Course properties ---

    [Fact]
    public void HasActiveCourse_FalseByDefault()
    {
        Assert.False(_nav.HasActiveCourse);
    }

    [Fact]
    public void ApplyCourseNextPointPosition_SetsActiveCourseTrue()
    {
        _nav.ApplyCourseNextPointPosition(48.0, 9.0);
        Assert.True(_nav.HasActiveCourse);
        Assert.Equal(48.0, _nav.CourseNextPointLatitude);
        Assert.Equal(9.0, _nav.CourseNextPointLongitude);
    }

    [Theory]
    [InlineData("navigation.courseGreatCircle.nextPoint.distance")]
    [InlineData("navigation.courseRhumbline.nextPoint.distance")]
    public void Apply_CourseDistance_SetsProperty(string path)
    {
        var je = JsonSerializer.SerializeToElement(5000.0);
        Assert.True(_nav.Apply(path, je));
        Assert.Equal(5000.0, _nav.CourseNextPointDistance);
    }

    [Theory]
    [InlineData("navigation.courseGreatCircle.nextPoint.bearingTrue")]
    [InlineData("navigation.courseRhumbline.nextPoint.bearingTrue")]
    public void Apply_CourseBearing_SetsProperty(string path)
    {
        var je = JsonSerializer.SerializeToElement(1.57);
        Assert.True(_nav.Apply(path, je));
        Assert.Equal(1.57, _nav.CourseNextPointBearing);
    }

    [Theory]
    [InlineData("navigation.courseGreatCircle.nextPoint.timeToGo")]
    [InlineData("navigation.courseRhumbline.nextPoint.timeToGo")]
    public void Apply_CourseTimeToGo_SetsProperty(string path)
    {
        var je = JsonSerializer.SerializeToElement(3600.0);
        Assert.True(_nav.Apply(path, je));
        Assert.Equal(3600.0, _nav.CourseNextPointTimeToGo);
    }

    [Theory]
    [InlineData("navigation.courseGreatCircle.nextPoint.velocityMadeGood")]
    [InlineData("navigation.courseRhumbline.nextPoint.velocityMadeGood")]
    public void Apply_CourseVmg_SetsProperty(string path)
    {
        var je = JsonSerializer.SerializeToElement(2.8);
        Assert.True(_nav.Apply(path, je));
        Assert.Equal(2.8, _nav.CourseNextPointVmg);
    }

    // --- ApplyString ---

    [Theory]
    [InlineData("navigation.courseGreatCircle.activeRoute.href", "/resources/routes/abc")]
    [InlineData("navigation.courseRhumbline.activeRoute.href", "/resources/routes/xyz")]
    public void ApplyString_RouteHref_SetsProperty(string path, string value)
    {
        Assert.True(_nav.ApplyString(path, value));
        Assert.Equal(value, _nav.ActiveRouteHref);
    }

    [Theory]
    [InlineData("navigation.courseGreatCircle.activeRoute.name", "To harbor")]
    [InlineData("navigation.courseRhumbline.activeRoute.name", "Sunday sail")]
    public void ApplyString_RouteName_SetsProperty(string path, string value)
    {
        Assert.True(_nav.ApplyString(path, value));
        Assert.Equal(value, _nav.ActiveRouteName);
    }

    [Fact]
    public void ApplyString_UnknownPath_ReturnsFalse()
    {
        Assert.False(_nav.ApplyString("some.unknown", "value"));
    }

    // --- Timestamp ---

    [Fact]
    public void SetTimestamp_SetsProperty()
    {
        _nav.SetTimestamp("2025-01-01T00:00:00Z");
        Assert.Equal("2025-01-01T00:00:00Z", _nav.LastTimestamp);
    }
}
