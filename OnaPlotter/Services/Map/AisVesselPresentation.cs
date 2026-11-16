using System.Globalization;
using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Map;

/// <summary>
/// Pure presentation rules for an AIS / radar target on the chart: name
/// fallbacks, colour roles, label and overlay visibility. Split out of
/// <see cref="AisLayerController"/> so each rule is unit-testable without a
/// store or a clock.
/// </summary>
public static class AisVesselPresentation
{
    private const string BuddyStar = "★ ";

    /// <summary>On-chart label: name, else MMSI, buddy-starred. Null when
    /// the vessel has neither (no label drawn).</summary>
    public static string? DisplayName(string? name, string? mmsi, bool buddy)
    {
        string? baseName = !string.IsNullOrEmpty(name) ? name
            : !string.IsNullOrEmpty(mmsi) ? mmsi
            : null;
        return baseName is null ? null : (buddy ? BuddyStar + baseName : baseName);
    }

    /// <summary>
    /// Popup title and CPA chip name. Precedence: name, callsign,
    /// "MMSI &lt;id&gt;" (the prefix keeps a bare 9-digit number from
    /// reading as a distance), "Unknown". Buddy-starred.
    /// </summary>
    public static string Title(string? name, string? callsign, string? mmsi, bool buddy)
    {
        string title = !string.IsNullOrEmpty(name) ? name
            : !string.IsNullOrEmpty(callsign) ? callsign
            : !string.IsNullOrEmpty(mmsi) ? "MMSI " + mmsi
            : "Unknown";
        return buddy ? BuddyStar + title : title;
    }

    /// <summary>
    /// Popup identity line, parts joined with " · ". Dimensions render
    /// as "12.5 × 4.2 m", "L 12.5 m" or "B 4.2 m" depending on what AIS
    /// static delivered (class B targets often send neither). Null when
    /// there is nothing to show.
    /// <para>Example: ("538071935", "V7A6238", "Sailing", 12.5, 4.2) -&gt;
    /// "MMSI 538071935 · Call V7A6238 · Sailing · 12.5 × 4.2 m"</para>
    /// </summary>
    public static string? Subtitle(string? mmsi, string? callsign, string? shipType,
        double? loaM, double? beamM)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrEmpty(mmsi)) parts.Add("MMSI " + mmsi);
        if (!string.IsNullOrEmpty(callsign)) parts.Add("Call " + callsign);
        if (!string.IsNullOrEmpty(shipType)) parts.Add(shipType);
        double? loa = loaM is double l && double.IsFinite(l) ? l : null;
        double? beam = beamM is double b && double.IsFinite(b) ? b : null;
        if (loa is not null && beam is not null) parts.Add($"{F1(loa.Value)} × {F1(beam.Value)} m");
        else if (loa is not null) parts.Add($"L {F1(loa.Value)} m");
        else if (beam is not null) parts.Add($"B {F1(beam.Value)} m");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>Marker colour role. Radar targets keep the radar tone
    /// unless in CPA alarm; buddies always win over the alarm (friendly
    /// boats, no red overlay even when close); then alarm; then the
    /// ship-type palette.</summary>
    public static AisTone MarkerTone(bool radar, bool buddy, bool alarm) =>
        radar ? (alarm ? AisTone.Danger : AisTone.Radar)
        : buddy ? AisTone.Buddy
        : alarm ? AisTone.Danger
        : AisTone.Ship;

    /// <summary>COG-vector colour role: the vessel's identity colour,
    /// never the alarm red.</summary>
    public static AisTone VectorTone(bool radar, bool buddy) =>
        radar ? AisTone.Radar : buddy ? AisTone.Buddy : AisTone.Ship;

    /// <summary>
    /// Whether the name label is drawn. Needs a display name; hidden past
    /// the helm's "AIS inactive" threshold so a chart full of ghost
    /// MMSIs doesn't drown the live targets, except for SART beacons and
    /// buddies (life-safety and "where's my friend" tolerate lateness).
    /// Harbour mode and the global labels toggle are applied by the
    /// renderer on top.
    /// </summary>
    public static bool LabelVisible(string? displayName, double ageSeconds,
        double inactiveSeconds, bool sart, bool buddy) =>
        displayName is not null && (ageSeconds < inactiveSeconds || sart || buddy);

    /// <summary>
    /// Closing-encounter overlay. Drawn only for a closing target
    /// (TCPA &gt; 0) with a projected CPA point; alarm tier always,
    /// awareness tier when the helm selects it.
    /// </summary>
    public static AisCpaOverlay CpaOverlay(Cpa.Threat threat, double? tcpaMin, bool hasCpaPoint)
    {
        if (tcpaMin is not > 0 || !hasCpaPoint) return AisCpaOverlay.None;
        return threat switch
        {
            Cpa.Threat.Alarm => AisCpaOverlay.Alarm,
            Cpa.Threat.Awareness => AisCpaOverlay.Awareness,
            _ => AisCpaOverlay.None,
        };
    }

    /// <summary>Chevron rotation: COG, else heading. SART beacons are
    /// not rotated.</summary>
    public static double? RotationRad(bool sart, double? cogRad, double? headingRad) =>
        sart ? null : cogRad ?? headingRad;

    /// <summary>Radians to whole degrees in [0, 360), null-propagating.</summary>
    public static double? WholeDegrees(double? rad)
    {
        if (rad is not double r || !double.IsFinite(r)) return null;
        double deg = Math.Round(r * 180.0 / Math.PI, MidpointRounding.AwayFromZero) % 360.0;
        return deg < 0 ? deg + 360.0 : deg;
    }

    /// <summary>Rounds for display precision, null-propagating.</summary>
    public static double? Round(double? v, int decimals) =>
        v is double d && double.IsFinite(d)
            ? Math.Round(d, decimals, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>True when the target is a radar ARPA track.</summary>
    public static bool IsRadar(AisVessel v) => v.Source == TargetSource.Radar;
}
