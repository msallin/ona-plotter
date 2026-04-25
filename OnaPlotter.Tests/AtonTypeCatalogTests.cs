using OnaPlotter.Services;

namespace OnaPlotter.Tests;

public class AtonTypeCatalogTests
{
    [Test]
    public async Task Lookup_RealCardinal_NorthThroughWest()
    {
        // Codes 9..12 are the real cardinal marks per AIS Type 21.
        await Assert.That(AtonTypeCatalog.Lookup(9))
            .IsEqualTo(new AtonTypeCatalog.AtonTypeInfo(
                AtonTypeCatalog.AtonSymbol.Cardinal,
                AtonTypeCatalog.AtonSide.North, false));
        await Assert.That(AtonTypeCatalog.Lookup(10).Side)
            .IsEqualTo(AtonTypeCatalog.AtonSide.East);
        await Assert.That(AtonTypeCatalog.Lookup(11).Side)
            .IsEqualTo(AtonTypeCatalog.AtonSide.South);
        await Assert.That(AtonTypeCatalog.Lookup(12).Side)
            .IsEqualTo(AtonTypeCatalog.AtonSide.West);
    }

    [Test]
    public async Task Lookup_RealLateral_PortAndStarboard()
    {
        await Assert.That(AtonTypeCatalog.Lookup(13))
            .IsEqualTo(new AtonTypeCatalog.AtonTypeInfo(
                AtonTypeCatalog.AtonSymbol.Lateral,
                AtonTypeCatalog.AtonSide.Port, false));
        await Assert.That(AtonTypeCatalog.Lookup(14).Side)
            .IsEqualTo(AtonTypeCatalog.AtonSide.Starboard);
    }

    [Test]
    public async Task Lookup_VirtualCardinal_HasVirtualHint()
    {
        // Codes 20..23 are virtual cardinals (AIS-broadcast only, no
        // physical buoy in the water). The catalog hints at this; the
        // AtoN's own .Virtual property is still authoritative.
        var north = AtonTypeCatalog.Lookup(20);
        await Assert.That(north.Symbol).IsEqualTo(AtonTypeCatalog.AtonSymbol.Cardinal);
        await Assert.That(north.Side).IsEqualTo(AtonTypeCatalog.AtonSide.North);
        await Assert.That(north.VirtualHint).IsTrue();
    }

    [Test]
    public async Task Lookup_VirtualLateral_HasVirtualHint()
    {
        var port = AtonTypeCatalog.Lookup(24);
        await Assert.That(port.Symbol).IsEqualTo(AtonTypeCatalog.AtonSymbol.Lateral);
        await Assert.That(port.Side).IsEqualTo(AtonTypeCatalog.AtonSide.Port);
        await Assert.That(port.VirtualHint).IsTrue();
    }

    [Test]
    public async Task Lookup_SpecialMarks()
    {
        await Assert.That(AtonTypeCatalog.Lookup(28).Symbol)
            .IsEqualTo(AtonTypeCatalog.AtonSymbol.IsolatedDanger);
        await Assert.That(AtonTypeCatalog.Lookup(29).Symbol)
            .IsEqualTo(AtonTypeCatalog.AtonSymbol.SafeWater);
        await Assert.That(AtonTypeCatalog.Lookup(30).Symbol)
            .IsEqualTo(AtonTypeCatalog.AtonSymbol.Special);
    }

    [Test]
    public async Task Lookup_BaseStationCode_MapsToBaseStation()
    {
        // -1 isn't a real AIS Type 21 code; it's the convention we
        // use internally (mirrored from Freeboard-SK) for
        // shore.basestations.* contexts so they share the AtoN
        // marker layer. Locked here so a future code-cleanup can't
        // drop it without realising base stations stop rendering.
        await Assert.That(AtonTypeCatalog.Lookup(-1).Symbol)
            .IsEqualTo(AtonTypeCatalog.AtonSymbol.BaseStation);
    }

    [Test]
    public async Task Lookup_UnknownCode_ReturnsUnknown()
    {
        // 99 is not in the AIS Type 21 catalog. The lookup must
        // degrade gracefully so a plugin sending an out-of-range
        // code doesn't crash the renderer.
        var result = AtonTypeCatalog.Lookup(99);
        await Assert.That(result.Symbol).IsEqualTo(AtonTypeCatalog.AtonSymbol.Unknown);
        await Assert.That(result.Side).IsEqualTo(AtonTypeCatalog.AtonSide.None);
        await Assert.That(result.VirtualHint).IsFalse();
    }

    [Test]
    public async Task Lookup_NullCode_ReturnsUnknown()
    {
        // First-sight AtoN: position arrived but type didn't yet.
        // Must render as "something unidentified" rather than crash.
        var result = AtonTypeCatalog.Lookup(null);
        await Assert.That(result.Symbol).IsEqualTo(AtonTypeCatalog.AtonSymbol.Unknown);
    }
}
