namespace OnaPlotter.Services;

/// <summary>
/// AIS Type 21 / IALA Region A AtoN type-id catalog. The numeric type
/// codes broadcast by an AIS AtoN station map to short symbol kinds
/// that drive icon selection on the map. Encoded here in C# (with
/// tests) per the dev-philosophy rule that decisions live in C#, not
/// inline in JS.
/// <para>
/// Region B (Americas / Japan / Korea / Philippines) inverts the
/// red/green sense of lateral marks vs Region A. Adding region-aware
/// rendering is a follow-up; today we render Region A which covers
/// most of Europe + Africa + Australia where OnaPlotter is being
/// used. The numeric type-id is region-agnostic per ITU-R M.1371; the
/// rendering layer can flip colours when we add the toggle.
/// </para>
/// </summary>
public static class AtonTypeCatalog
{
    /// <summary>Coarse symbol category used by the map renderer to pick
    /// an icon. Stays small so the JS side can fan out to a tight set
    /// of pre-baked SVGs.</summary>
    public enum AtonSymbol
    {
        /// <summary>Unknown / not yet received / no mapping. Falls back
        /// to a generic mark.</summary>
        Unknown,
        Cardinal,    // 9-12 / 20-23: north / east / south / west
        Lateral,     // 13-14 / 24-25: port / starboard
        IsolatedDanger,  // 28
        SafeWater,   // 29
        Special,     // 30
        BaseStation, // -1: not strictly an AtoN, but the SignalK
                     // shore.basestations.* tier reuses this catalog
                     // and renders here too.
    }

    /// <summary>Lateral side. Used to flip port/starboard colour
    /// when we render. North/East/South/West cardinal direction is
    /// also expressed here for code 9-12 / 20-23.</summary>
    public enum AtonSide
    {
        None,
        North, East, South, West,    // cardinal
        Port, Starboard,             // lateral
    }

    /// <summary>One row of the catalog: numeric type-id maps to a
    /// (symbol, side, virtual?) triple. Virtual is a hint - the
    /// AtoN's own <c>Virtual</c> property is authoritative; the
    /// 20-25 codes happen to be pre-marked virtual in AIS Type 21
    /// but a plugin can publish a different shape.</summary>
    public readonly record struct AtonTypeInfo(
        AtonSymbol Symbol,
        AtonSide Side,
        bool VirtualHint);

    /// <summary>Looks up a numeric AtoN type-id. Returns
    /// <c>(Unknown, None, false)</c> for any code not in the
    /// catalog so callers don't have to null-check.</summary>
    public static AtonTypeInfo Lookup(int? typeId) => typeId switch
    {
        // -1 base station (SignalK shore.basestations.* convention)
        -1 => new(AtonSymbol.BaseStation, AtonSide.None, false),
        // Real cardinal marks (lit/topmark)
        9  => new(AtonSymbol.Cardinal, AtonSide.North,    false),
        10 => new(AtonSymbol.Cardinal, AtonSide.East,     false),
        11 => new(AtonSymbol.Cardinal, AtonSide.South,    false),
        12 => new(AtonSymbol.Cardinal, AtonSide.West,     false),
        // Real lateral
        13 => new(AtonSymbol.Lateral,  AtonSide.Port,     false),
        14 => new(AtonSymbol.Lateral,  AtonSide.Starboard,false),
        // Virtual cardinal (AIS-only, no physical mark)
        20 => new(AtonSymbol.Cardinal, AtonSide.North,    true),
        21 => new(AtonSymbol.Cardinal, AtonSide.East,     true),
        22 => new(AtonSymbol.Cardinal, AtonSide.South,    true),
        23 => new(AtonSymbol.Cardinal, AtonSide.West,     true),
        // Virtual lateral
        24 => new(AtonSymbol.Lateral,  AtonSide.Port,     true),
        25 => new(AtonSymbol.Lateral,  AtonSide.Starboard,true),
        // Special purpose marks
        28 => new(AtonSymbol.IsolatedDanger, AtonSide.None, false),
        29 => new(AtonSymbol.SafeWater,      AtonSide.None, false),
        30 => new(AtonSymbol.Special,        AtonSide.None, false),
        _  => new(AtonSymbol.Unknown,        AtonSide.None, false),
    };
}
