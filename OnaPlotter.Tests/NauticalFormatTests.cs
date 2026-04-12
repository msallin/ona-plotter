using OnaPlotter.Models;

namespace OnaPlotter.Tests;

public class NauticalFormatTests
{
    [Test]
    public async Task FormatLat_Null_ReturnsDashes()
    {
        await Assert.That(NauticalFormat.FormatLat(null)).IsEqualTo("--");
    }

    [Test]
    public async Task FormatLon_Null_ReturnsDashes()
    {
        await Assert.That(NauticalFormat.FormatLon(null)).IsEqualTo("--");
    }

    [Test]
    public async Task FormatLat_Positive_HasNorthHemisphere()
    {
        string result = NauticalFormat.FormatLat(47.390933);
        await Assert.That(result).EndsWith("N");
        await Assert.That(result).StartsWith("47\u00b0");
    }

    [Test]
    public async Task FormatLat_Negative_HasSouthHemisphere()
    {
        string result = NauticalFormat.FormatLat(-33.8688);
        await Assert.That(result).EndsWith("S");
    }

    [Test]
    public async Task FormatLon_Positive_HasEastHemisphere()
    {
        string result = NauticalFormat.FormatLon(8.54);
        await Assert.That(result).EndsWith("E");
    }

    [Test]
    public async Task FormatLon_Negative_HasWestHemisphere()
    {
        string result = NauticalFormat.FormatLon(-122.4194);
        await Assert.That(result).EndsWith("W");
    }

    [Test]
    [Arguments(47.390933, "47\u00b023.456'N")]
    public async Task FormatLat_KnownValue_CorrectDDMM(double deg, string expected)
    {
        await Assert.That(NauticalFormat.FormatLat(deg)).IsEqualTo(expected);
    }

    [Test]
    public async Task FormatLon_ThreeDigitDegrees()
    {
        string result = NauticalFormat.FormatLon(8.0);
        await Assert.That(result).StartsWith("008\u00b0");
    }

    [Test]
    public async Task FormatPosition_BothNull_ReturnsDashes()
    {
        await Assert.That(NauticalFormat.FormatPosition(null, null)).IsEqualTo("--");
    }

    [Test]
    public async Task FormatPosition_OneNull_ReturnsDashes()
    {
        await Assert.That(NauticalFormat.FormatPosition(47.0, null)).IsEqualTo("--");
        await Assert.That(NauticalFormat.FormatPosition(null, 8.0)).IsEqualTo("--");
    }

    [Test]
    public async Task FormatPosition_ValidValues_ContainsBothParts()
    {
        string result = NauticalFormat.FormatPosition(47.0, 8.0);
        await Assert.That(result).Contains("N");
        await Assert.That(result).Contains("E");
    }
}
