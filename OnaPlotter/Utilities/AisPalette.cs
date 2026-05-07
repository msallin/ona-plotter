namespace OnaPlotter.Utilities;

/// <summary>
/// Central AIS visual catalogue: ship-type -> colour, ship-type ->
/// coarse-category (for the inside glyph). Lives on the C# side so
/// the tests exercise the classifier and the JS receives fully-resolved
/// values in the push payload - no duplicate palette to drift out of
/// sync between client and renderer.
///
/// <para>Palette is tuned for blue water: all warm earth hues so every
/// vessel reads clearly against OSM/OpenSeaMap tiles. Change here,
/// it's reflected in both the map markers and the Layers-panel list.</para>
/// </summary>
public static class AisPalette
{
    // Named constants so callers can reference specific hues without
    // reaching into the dictionary.
    public const string Cargo      = "#7d9b76";  // sage green - commercial bulk
    public const string Tanker     = "#c9a27e";  // warm tan - oil / liquid
    public const string Passenger  = "#b589b0";  // muted plum - civilian
    public const string Fishing    = "#d4a850";  // muted gold - nets
    public const string Sailing    = "#e28862";  // warm coral
    public const string Pleasure   = "#f2b785";  // pale apricot
    public const string Tug        = "#c0a080";  // warm beige
    public const string Military   = "#8a7a7a";  // muted brown-gray
    public const string Sar        = "#d17056";  // terracotta ("rescue orange" toned down)
    public const string Default    = "#e0c9a6";  // warm cream for unclassified
    public const string Danger     = "#c4453e";  // warm brick - CPA alarm
    public const string Buddy      = "#e9c46a";  // honey gold

    /// <summary>
    /// Pick the rendering colour for an AIS target. Buddies always win
    /// (intentional friendly boats - no red overlay even if close);
    /// danger overlays second (CPA alarm active); ship-type third;
    /// unknown fourth.
    /// </summary>
    public static string Color(string? shipType, bool isDanger, bool isBuddy)
    {
        if (isBuddy) return Buddy;
        if (isDanger) return Danger;
        return ShipTypeColor(shipType);
    }

    /// <summary>Maps a SignalK <c>design.aisShipType</c> string to the
    /// closest palette entry by substring match. Returns the Default
    /// cream for unrecognised or null types.</summary>
    public static string ShipTypeColor(string? shipType)
    {
        if (string.IsNullOrWhiteSpace(shipType)) return Default;
        var t = shipType.ToLowerInvariant();
        if (t.Contains("cargo"))     return Cargo;
        if (t.Contains("tanker"))    return Tanker;
        if (t.Contains("passenger")) return Passenger;
        if (t.Contains("fishing"))   return Fishing;
        if (t.Contains("sailing"))   return Sailing;
        if (t.Contains("pleasure"))  return Pleasure;
        if (t.Contains("tug"))       return Tug;
        if (t.Contains("military"))  return Military;
        if (t.Contains("sar"))       return Sar;
        return Default;
    }

    /// <summary>
    /// Coarse shape-glyph category used on the AIS chevron for colour-
    /// blind accessibility. Four buckets (sail / fish / commercial /
    /// service); unknown returns null so the chevron renders plain.
    /// </summary>
    public static string? ShipTypeCategory(string? shipType)
    {
        if (string.IsNullOrWhiteSpace(shipType)) return null;
        var t = shipType.ToLowerInvariant();
        if (t.Contains("sail"))     return "sail";
        if (t.Contains("pleasure")) return "sail";
        if (t.Contains("fish"))     return "fish";
        if (t.Contains("cargo"))    return "commercial";
        if (t.Contains("tanker"))   return "commercial";
        if (t.Contains("passenger"))return "commercial";
        if (t.Contains("tug"))      return "commercial";
        if (t.Contains("military")) return "service";
        if (t.Contains("sar"))      return "service";
        return null;
    }
}
