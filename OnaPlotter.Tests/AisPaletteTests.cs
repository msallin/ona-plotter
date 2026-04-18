using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the AIS palette: the C# side is the source of truth for
/// ship-type -> colour and ship-type -> glyph category. The JS
/// side reads these off the push payload, so a regression here is
/// immediately visible on the chart.
/// </summary>
public class AisPaletteTests
{
    // --- ShipTypeColor ---

    [Test]
    [Arguments("Cargo", AisPalette.Cargo)]
    [Arguments("Cargo ship, Hazardous category A", AisPalette.Cargo)]
    [Arguments("Tanker", AisPalette.Tanker)]
    [Arguments("Passenger ship", AisPalette.Passenger)]
    [Arguments("Fishing", AisPalette.Fishing)]
    [Arguments("Sailing", AisPalette.Sailing)]
    [Arguments("Pleasure craft", AisPalette.Pleasure)]
    [Arguments("Tug", AisPalette.Tug)]
    [Arguments("Military ops", AisPalette.Military)]
    [Arguments("SAR", AisPalette.Sar)]
    public async Task ShipTypeColor_KnownTypes(string shipType, string expected)
        => await Assert.That(AisPalette.ShipTypeColor(shipType)).IsEqualTo(expected);

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("Unknown experimental kite-ship")]
    public async Task ShipTypeColor_UnknownOrEmpty_ReturnsDefault(string? shipType)
        => await Assert.That(AisPalette.ShipTypeColor(shipType)).IsEqualTo(AisPalette.Default);

    // --- Color priority (buddy > danger > ship-type > default) ---

    [Test]
    public async Task Color_Buddy_AlwaysWins()
    {
        // Even with danger flag on a known ship-type, buddy takes priority.
        // Mirror of the rule in aisColor(): buddies don't get red tint.
        await Assert.That(AisPalette.Color("Cargo", isDanger: true, isBuddy: true))
            .IsEqualTo(AisPalette.Buddy);
    }

    [Test]
    public async Task Color_Danger_BeatsShipType()
    {
        await Assert.That(AisPalette.Color("Cargo", isDanger: true, isBuddy: false))
            .IsEqualTo(AisPalette.Danger);
    }

    [Test]
    public async Task Color_NoOverride_UsesShipType()
    {
        await Assert.That(AisPalette.Color("Tanker", isDanger: false, isBuddy: false))
            .IsEqualTo(AisPalette.Tanker);
    }

    [Test]
    public async Task Color_UnknownShipType_UsesDefault()
    {
        await Assert.That(AisPalette.Color(null, isDanger: false, isBuddy: false))
            .IsEqualTo(AisPalette.Default);
    }

    // --- ShipTypeCategory (glyph bucket) ---

    [Test]
    [Arguments("Sailing", "sail")]
    [Arguments("Pleasure craft", "sail")]
    [Arguments("Fishing", "fish")]
    [Arguments("Cargo", "commercial")]
    [Arguments("Tanker", "commercial")]
    [Arguments("Passenger", "commercial")]
    [Arguments("Tug", "commercial")]
    [Arguments("Military", "service")]
    [Arguments("SAR", "service")]
    public async Task ShipTypeCategory_KnownTypes(string shipType, string expected)
        => await Assert.That(AisPalette.ShipTypeCategory(shipType)).IsEqualTo(expected);

    [Test]
    public async Task ShipTypeCategory_Unknown_ReturnsNull()
    {
        await Assert.That(AisPalette.ShipTypeCategory(null)).IsNull();
        await Assert.That(AisPalette.ShipTypeCategory("")).IsNull();
        await Assert.That(AisPalette.ShipTypeCategory("Dredger")).IsNull();
    }
}
