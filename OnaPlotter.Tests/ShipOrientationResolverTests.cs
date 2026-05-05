using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class ShipOrientationResolverTests
{
    private static NavigationData NewNav(
        double? hdgTrue = null, double? hdgMag = null,
        double? cogTrue = null, double? cogMag = null)
    {
        var d = new NavigationData();
        if (hdgTrue is double ht) d.Apply("navigation.headingTrue", ht);
        if (hdgMag is double hm) d.Apply("navigation.headingMagnetic", hm);
        if (cogTrue is double ct) d.Apply("navigation.courseOverGroundTrue", ct);
        if (cogMag is double cm) d.Apply("navigation.courseOverGroundMagnetic", cm);
        return d;
    }

    // -- Parse / ToSetting round-trip ------------------------------

    [Test]
    public async Task Parse_KnownStrings_RoundTrip()
    {
        await Assert.That(ShipOrientationResolver.Parse("headingTrue")).IsEqualTo(ShipOrientationSource.HeadingTrue);
        await Assert.That(ShipOrientationResolver.Parse("headingMagnetic")).IsEqualTo(ShipOrientationSource.HeadingMagnetic);
        await Assert.That(ShipOrientationResolver.Parse("cogTrue")).IsEqualTo(ShipOrientationSource.CogTrue);
        await Assert.That(ShipOrientationResolver.Parse("cogMagnetic")).IsEqualTo(ShipOrientationSource.CogMagnetic);
    }

    [Test]
    public async Task Parse_CaseInsensitive_AndCommonAliases()
    {
        await Assert.That(ShipOrientationResolver.Parse("HEADINGMAGNETIC")).IsEqualTo(ShipOrientationSource.HeadingMagnetic);
        await Assert.That(ShipOrientationResolver.Parse("hdgMag")).IsEqualTo(ShipOrientationSource.HeadingMagnetic);
        await Assert.That(ShipOrientationResolver.Parse("cogmag")).IsEqualTo(ShipOrientationSource.CogMagnetic);
    }

    [Test]
    public async Task Parse_UnknownOrEmpty_DefaultsToHeadingTrue()
    {
        await Assert.That(ShipOrientationResolver.Parse(null)).IsEqualTo(ShipOrientationSource.HeadingTrue);
        await Assert.That(ShipOrientationResolver.Parse("")).IsEqualTo(ShipOrientationSource.HeadingTrue);
        await Assert.That(ShipOrientationResolver.Parse("garbage")).IsEqualTo(ShipOrientationSource.HeadingTrue);
    }

    [Test]
    public async Task ToSetting_AllValues_RoundTrip()
    {
        foreach (var s in Enum.GetValues<ShipOrientationSource>())
        {
            await Assert.That(ShipOrientationResolver.Parse(ShipOrientationResolver.ToSetting(s))).IsEqualTo(s);
        }
    }

    // -- Resolve happy paths --------------------------------------

    [Test]
    public async Task Resolve_PickedFieldPresent_ReturnsThat()
    {
        var d = NewNav(hdgTrue: 1.0, hdgMag: 1.5, cogTrue: 2.0, cogMag: 2.5);
        await Assert.That(ShipOrientationResolver.Resolve(ShipOrientationSource.HeadingTrue, d)).IsEqualTo(1.0);
        await Assert.That(ShipOrientationResolver.Resolve(ShipOrientationSource.HeadingMagnetic, d)).IsEqualTo(1.5);
        await Assert.That(ShipOrientationResolver.Resolve(ShipOrientationSource.CogTrue, d)).IsEqualTo(2.0);
        await Assert.That(ShipOrientationResolver.Resolve(ShipOrientationSource.CogMagnetic, d)).IsEqualTo(2.5);
    }

    [Test]
    public async Task Resolve_SmoothedCog_OverridesCogVariant()
    {
        // When the picked source is a COG variant, the smoothed
        // override wins -- the boat icon stops twitching even though
        // the raw COG is still reported by the server.
        var d = NewNav(cogTrue: 2.0);
        var smoothed = 1.95;
        await Assert.That(ShipOrientationResolver.Resolve(
            ShipOrientationSource.CogTrue, d, smoothedCog: smoothed)).IsEqualTo(smoothed);
    }

    [Test]
    public async Task Resolve_SmoothedCog_DoesNotAffectHeadingPicks()
    {
        // Heading picks pull from the heading fields directly --
        // a smoothed-COG argument is irrelevant.
        var d = NewNav(hdgTrue: 1.0);
        await Assert.That(ShipOrientationResolver.Resolve(
            ShipOrientationSource.HeadingTrue, d, smoothedCog: 99.0)).IsEqualTo(1.0);
    }

    // -- Fallback chains ------------------------------------------

    [Test]
    public async Task Resolve_HeadingTrueMissing_FallsBackToHeadingMagnetic()
    {
        var d = NewNav(hdgMag: 1.5, cogTrue: 2.0);
        await Assert.That(ShipOrientationResolver.Resolve(ShipOrientationSource.HeadingTrue, d)).IsEqualTo(1.5);
    }

    [Test]
    public async Task Resolve_HeadingFamilyMissing_FallsBackToCog()
    {
        var d = NewNav(cogTrue: 2.0, cogMag: 2.5);
        // HeadingTrue picked: HT -> HM -> CT -> CM. HT, HM null; lands at CT = 2.0.
        await Assert.That(ShipOrientationResolver.Resolve(ShipOrientationSource.HeadingTrue, d)).IsEqualTo(2.0);
        // HeadingMagnetic picked: HM -> HT -> CM -> CT. Lands at CM = 2.5.
        await Assert.That(ShipOrientationResolver.Resolve(ShipOrientationSource.HeadingMagnetic, d)).IsEqualTo(2.5);
    }

    [Test]
    public async Task Resolve_OnlyOneFieldPublished_AlwaysReturnsThat()
    {
        // A boat publishing only HeadingMagnetic: any pick falls
        // through the chain to that field.
        var d = NewNav(hdgMag: 1.5);
        foreach (var s in Enum.GetValues<ShipOrientationSource>())
        {
            await Assert.That(ShipOrientationResolver.Resolve(s, d)).IsEqualTo(1.5);
        }
    }

    [Test]
    public async Task Resolve_NoneOfTheFieldsPublished_ReturnsNull()
    {
        var d = NewNav();
        foreach (var s in Enum.GetValues<ShipOrientationSource>())
        {
            await Assert.That(ShipOrientationResolver.Resolve(s, d)).IsNull();
        }
    }

    [Test]
    public async Task Resolve_CogPickedButOnlyHdgPublished_FallsThroughCorrectly()
    {
        // CogMagnetic picked: CM -> CT -> HM -> HT. With hdgMag=1.5 only,
        // chain stops at HM.
        var d = NewNav(hdgMag: 1.5);
        await Assert.That(ShipOrientationResolver.Resolve(ShipOrientationSource.CogMagnetic, d)).IsEqualTo(1.5);
    }
}
