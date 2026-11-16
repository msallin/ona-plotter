using System.Text.Json.Serialization;

namespace OnaPlotter.Services.Map;

/// <summary>
/// One AIS push to <c>aisLayer.updateAisTargets</c>. A full snapshot
/// (<see cref="BaseSeq"/> = -1) replaces the renderer's state; a delta
/// carries only vessels whose rendered state changed since
/// <see cref="BaseSeq"/>, plus the contexts that left the chart. The
/// renderer refuses a delta whose base it doesn't hold (map re-init,
/// deferred update) and <see cref="AisPushService"/> answers with a
/// full snapshot.
/// </summary>
/// <param name="Seq">Sequence number of this push.</param>
/// <param name="BaseSeq">Sequence the delta applies on top of; -1 for a
/// full snapshot.</param>
/// <param name="NowUtc">C# clock at build time (ISO 8601). The popup's
/// "N min ago" is measured against it so client clock skew can't
/// change the reading.</param>
/// <param name="Vessels">New or changed vessels.</param>
/// <param name="Removed">Contexts to drop from the chart.</param>
public sealed record AisUpdatePayload(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("baseSeq")] int BaseSeq,
    [property: JsonPropertyName("nowUtc")] string NowUtc,
    [property: JsonPropertyName("vessels")] AisVesselPayload[] Vessels,
    [property: JsonPropertyName("removed")] string[] Removed)
{
    /// <summary><see cref="BaseSeq"/> value marking a full snapshot.</summary>
    public const int FullSnapshot = -1;

    /// <summary>True when this push replaces the renderer's state.</summary>
    [JsonIgnore] public bool IsFull => BaseSeq == FullSnapshot;
}

/// <summary>Theme colour role for a marker or COG vector. The renderer
/// maps roles to the live <c>--map-*</c> palette (so night mode
/// re-themes them); <see cref="Ship"/> uses the vessel's
/// <see cref="AisVesselStatic.ShipColor"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AisTone>))]
public enum AisTone
{
    [JsonStringEnumMemberName("ship")] Ship,
    [JsonStringEnumMemberName("buddy")] Buddy,
    [JsonStringEnumMemberName("danger")] Danger,
    [JsonStringEnumMemberName("radar")] Radar,
}

/// <summary>Closing-encounter overlay for a target. <see cref="Alarm"/>
/// always draws; <see cref="Awareness"/> draws only while the helm has
/// the target click-selected (a renderer-local UI state).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AisCpaOverlay>))]
public enum AisCpaOverlay
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("alarm")] Alarm,
    [JsonStringEnumMemberName("awareness")] Awareness,
}

/// <summary>
/// Identity fields that change only when AIS static data, the buddy list
/// or the target source changes. Sent with a vessel on a full snapshot
/// and whenever it differs from what the renderer holds; otherwise
/// omitted so a 200-vessel harbour doesn't resend names every tick.
/// </summary>
public sealed record AisVesselStatic
{
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("mmsi"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mmsi { get; init; }

    [JsonPropertyName("buddy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Buddy { get; init; }

    /// <summary>Radar ARPA target: outline-triangle icon, no buddy
    /// affordance.</summary>
    [JsonPropertyName("radar"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Radar { get; init; }

    /// <summary>"SART" / "MOB" / "EPIRB" or null.</summary>
    [JsonPropertyName("sartCategory"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SartCategory { get; init; }

    /// <summary>"sail" / "fish" / "commercial" / "service" or null:
    /// glyph drawn inside the chevron.</summary>
    [JsonPropertyName("glyphCategory"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GlyphCategory { get; init; }

    /// <summary>Ship-type palette colour for <see cref="AisTone.Ship"/>.</summary>
    [JsonPropertyName("shipColor")]
    public string ShipColor { get; init; } = Utilities.AisPalette.Default;

    /// <summary>On-chart label (name, else MMSI, buddy-starred), or null
    /// when the vessel has neither.</summary>
    [JsonPropertyName("displayName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    /// <summary>Popup title and CPA chip name.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    /// <summary>Popup identity line: "MMSI 538071935 · Call V7A6238 ·
    /// Sailing · 12.5 × 4.2 m", or null when nothing is known.</summary>
    [JsonPropertyName("subtitle"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subtitle { get; init; }
}

/// <summary>
/// Rendered state of one vessel. Every presentation decision (tone,
/// opacity, label visibility, CPA overlay, popup numbers) is resolved
/// here so <c>aisLayer.js</c> only applies values. The record is the
/// complete dynamic state: a null field means "absent" (e.g. no COG
/// vector), not "unchanged". <see cref="Static"/> and the trail fields
/// are the exceptions, see their docs.
/// </summary>
public sealed record AisVesselPayload
{
    private const JsonIgnoreCondition SkipNull = JsonIgnoreCondition.WhenWritingNull;
    private const JsonIgnoreCondition SkipDefault = JsonIgnoreCondition.WhenWritingDefault;

    [JsonPropertyName("context")] public string Context { get; init; } = "";

    /// <summary>Identity fields when they changed (or on a full
    /// snapshot); null means "keep what you have".</summary>
    [JsonPropertyName("static"), JsonIgnore(Condition = SkipNull)]
    public AisVesselStatic? Static { get; init; }

    [JsonPropertyName("lat")] public double Lat { get; init; }
    [JsonPropertyName("lon")] public double Lon { get; init; }

    /// <summary>Chevron rotation (COG, else heading), null for SART
    /// beacons and targets with neither.</summary>
    [JsonPropertyName("rotationRad"), JsonIgnore(Condition = SkipNull)]
    public double? RotationRad { get; init; }

    [JsonPropertyName("markerTone")] public AisTone MarkerTone { get; init; }

    /// <summary>COG vector colour role. Keeps the vessel's identity
    /// colour when the CPA alarm turns the marker red: the vector says
    /// where it's heading, the marker carries the alarm.</summary>
    [JsonPropertyName("vectorTone")] public AisTone VectorTone { get; init; }

    /// <summary>CPA-alarm pulse ring around the marker.</summary>
    [JsonPropertyName("pulse"), JsonIgnore(Condition = SkipDefault)]
    public bool Pulse { get; init; }

    /// <summary>Inline marker opacity from
    /// <see cref="Utilities.StalenessOpacity"/>, or null for full
    /// opacity (fresh targets, SART beacons, buddies).</summary>
    [JsonPropertyName("opacity"), JsonIgnore(Condition = SkipNull)]
    public string? Opacity { get; init; }

    /// <summary>Name label shown (subject to the renderer's global
    /// harbour-mode and labels toggles).</summary>
    [JsonPropertyName("labelVisible"), JsonIgnore(Condition = SkipDefault)]
    public bool LabelVisible { get; init; }

    /// <summary>COG-vector endpoint, null when stationary or COG / SOG
    /// unknown.</summary>
    [JsonPropertyName("vectorEndLat"), JsonIgnore(Condition = SkipNull)]
    public double? VectorEndLat { get; init; }
    [JsonPropertyName("vectorEndLon"), JsonIgnore(Condition = SkipNull)]
    public double? VectorEndLon { get; init; }

    /// <summary>"none" / "awareness" / "alarm" threat band.</summary>
    [JsonPropertyName("cpaThreat")] public string CpaThreat { get; init; } = "none";

    [JsonPropertyName("cpaOverlay")] public AisCpaOverlay CpaOverlay { get; init; }

    /// <summary>CPA (nm, 2 decimals) and TCPA (min, whole), present only
    /// for a closing target (TCPA &gt; 0).</summary>
    [JsonPropertyName("cpaNm"), JsonIgnore(Condition = SkipNull)]
    public double? CpaNm { get; init; }
    [JsonPropertyName("tcpaMin"), JsonIgnore(Condition = SkipNull)]
    public double? TcpaMin { get; init; }

    /// <summary>Target's projected position at TCPA, present whenever
    /// <see cref="CpaOverlay"/> is not None.</summary>
    [JsonPropertyName("cpaPointLat"), JsonIgnore(Condition = SkipNull)]
    public double? CpaPointLat { get; init; }
    [JsonPropertyName("cpaPointLon"), JsonIgnore(Condition = SkipNull)]
    public double? CpaPointLon { get; init; }

    /// <summary>Own ship's projected position at the same TCPA; null
    /// without an own fix (overlay degrades to target side only).</summary>
    [JsonPropertyName("ownCpaPointLat"), JsonIgnore(Condition = SkipNull)]
    public double? OwnCpaPointLat { get; init; }
    [JsonPropertyName("ownCpaPointLon"), JsonIgnore(Condition = SkipNull)]
    public double? OwnCpaPointLon { get; init; }

    [JsonPropertyName("colregsLabel"), JsonIgnore(Condition = SkipNull)]
    public string? ColregsLabel { get; init; }
    [JsonPropertyName("colregsRole"), JsonIgnore(Condition = SkipNull)]
    public string? ColregsRole { get; init; }
    /// <summary>Own ship is the give-way vessel.</summary>
    [JsonPropertyName("colregsGiveWay"), JsonIgnore(Condition = SkipDefault)]
    public bool ColregsGiveWay { get; init; }

    /// <summary>Popup offers "Snooze alarm" (threatening, not a buddy).</summary>
    [JsonPropertyName("snoozable"), JsonIgnore(Condition = SkipDefault)]
    public bool Snoozable { get; init; }

    /// <summary>Popup values, rounded to their display precision so an
    /// unchanged reading doesn't count as a change.</summary>
    [JsonPropertyName("sogKn"), JsonIgnore(Condition = SkipNull)]
    public double? SogKn { get; init; }
    [JsonPropertyName("cogDeg"), JsonIgnore(Condition = SkipNull)]
    public double? CogDeg { get; init; }
    [JsonPropertyName("headingDeg"), JsonIgnore(Condition = SkipNull)]
    public double? HeadingDeg { get; init; }
    /// <summary>Range (nm) and bearing (deg true) from own ship; null
    /// without an own fix.</summary>
    [JsonPropertyName("rangeNm"), JsonIgnore(Condition = SkipNull)]
    public double? RangeNm { get; init; }
    [JsonPropertyName("bearingDeg"), JsonIgnore(Condition = SkipNull)]
    public double? BearingDeg { get; init; }

    /// <summary>Most recent AIS evidence, ISO 8601 UTC.</summary>
    [JsonPropertyName("lastAisSeen")] public string LastAisSeen { get; init; } = "";

    /// <summary>Whole trail as flat <c>[lat0, lon0, ...]</c>, replacing
    /// the renderer's copy. Set on a full snapshot.</summary>
    [JsonPropertyName("trail"), JsonIgnore(Condition = SkipNull)]
    public double[]? Trail { get; init; }

    /// <summary>Points to append to the renderer's trail after
    /// <see cref="TrailTrim"/>.</summary>
    [JsonPropertyName("trailAppend"), JsonIgnore(Condition = SkipNull)]
    public double[]? TrailAppend { get; init; }

    /// <summary>Points to drop from the head of the renderer's trail
    /// (clamped to what it holds).</summary>
    [JsonPropertyName("trailTrim"), JsonIgnore(Condition = SkipDefault)]
    public int TrailTrim { get; init; }
}
