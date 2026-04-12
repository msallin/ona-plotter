using OnaPlotter.Models;

namespace OnaPlotter.Tests;

public class NauticalFormatTests
{
    [Fact]
    public void FormatLat_Null_ReturnsDashes()
    {
        Assert.Equal("--", NauticalFormat.FormatLat(null));
    }

    [Fact]
    public void FormatLon_Null_ReturnsDashes()
    {
        Assert.Equal("--", NauticalFormat.FormatLon(null));
    }

    [Fact]
    public void FormatLat_Positive_HasNorthHemisphere()
    {
        string result = NauticalFormat.FormatLat(47.390933);
        Assert.EndsWith("N", result);
        Assert.StartsWith("47\u00b0", result);
    }

    [Fact]
    public void FormatLat_Negative_HasSouthHemisphere()
    {
        string result = NauticalFormat.FormatLat(-33.8688);
        Assert.EndsWith("S", result);
    }

    [Fact]
    public void FormatLon_Positive_HasEastHemisphere()
    {
        string result = NauticalFormat.FormatLon(8.54);
        Assert.EndsWith("E", result);
    }

    [Fact]
    public void FormatLon_Negative_HasWestHemisphere()
    {
        string result = NauticalFormat.FormatLon(-122.4194);
        Assert.EndsWith("W", result);
    }

    [Theory]
    [InlineData(47.390933, "47\u00b023.456'N")]
    public void FormatLat_KnownValue_CorrectDDMM(double deg, string expected)
    {
        Assert.Equal(expected, NauticalFormat.FormatLat(deg));
    }

    [Fact]
    public void FormatLon_ThreeDigitDegrees()
    {
        // Longitude degrees should be zero-padded to 3 digits.
        string result = NauticalFormat.FormatLon(8.0);
        Assert.StartsWith("008\u00b0", result);
    }

    [Fact]
    public void FormatPosition_BothNull_ReturnsDashes()
    {
        Assert.Equal("--", NauticalFormat.FormatPosition(null, null));
    }

    [Fact]
    public void FormatPosition_OneNull_ReturnsDashes()
    {
        Assert.Equal("--", NauticalFormat.FormatPosition(47.0, null));
        Assert.Equal("--", NauticalFormat.FormatPosition(null, 8.0));
    }

    [Fact]
    public void FormatPosition_ValidValues_ContainsBothParts()
    {
        string result = NauticalFormat.FormatPosition(47.0, 8.0);
        Assert.Contains("N", result);
        Assert.Contains("E", result);
    }
}
