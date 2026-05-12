using System.Text.Json.Serialization;

namespace OnaPlotter.Services.Map;

/// <summary>
/// Mutable JS-interop payload for the per-tick AIS push. One instance
/// per visible vessel in the snapshot, mutated in place from a pool
/// inside <see cref="AisPushService"/> so a 200-vessel harbour at 3 Hz
/// stops allocating ~600 anonymous-type heap objects per second.
///
/// <para>Wire format: explicit <c>[JsonPropertyName]</c> on every
/// property pins the camelCase JSON keys the existing
/// <c>aisLayer.js / updateAisTargets</c> consumer expects. Without
/// these annotations Blazor's interop serializer would default to
/// PascalCase and break the JS reader; pinning them here keeps the
/// contract self-documenting and survives a future
/// <see cref="System.Text.Json.JsonSerializerOptions.PropertyNamingPolicy"/>
/// flip.</para>
///
/// <para>Mutability: PascalCase properties with public setters because
/// the pool resets fields in place every push. Per-frame the same
/// instance gets re-filled with the next tick's data; the array
/// returned to JS interop is a fresh exact-sized array, but its
/// elements are pool refs.</para>
///
/// <para><b>Pool contract:</b> callers must NOT hold references to
/// payload instances past the next <c>BuildSnapshot</c> call. The
/// service consumes them synchronously through
/// <see cref="IMapAisJs.UpdateAisTargetsAsync"/> inside the same
/// PushAsync call, then releases.</para>
/// </summary>
public sealed class AisVesselPayload
{
    [JsonPropertyName("context")] public string? Context { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("mmsi")] public string? Mmsi { get; set; }
    [JsonPropertyName("callsign")] public string? Callsign { get; set; }
    /// <summary>Pre-resolved label: name -> mmsi -> null, with buddy
    /// star prefix. Lifted out of JS so the chart label and any other
    /// label-rendering surface share one fallback chain.</summary>
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("lat")] public double Lat { get; set; }
    [JsonPropertyName("lon")] public double Lon { get; set; }
    [JsonPropertyName("headingRad")] public double? HeadingRad { get; set; }
    [JsonPropertyName("cogRad")] public double? CogRad { get; set; }
    [JsonPropertyName("sogMs")] public double? SogMs { get; set; }
    [JsonPropertyName("shipType")] public string? ShipType { get; set; }
    /// <summary>AIS-static length overall (metres). Often absent;
    /// see AisVessel.LengthOverallMeters for the source.</summary>
    [JsonPropertyName("loaM")] public double? LoaM { get; set; }
    [JsonPropertyName("beamM")] public double? BeamM { get; set; }
    [JsonPropertyName("buddy")] public bool Buddy { get; set; }
    /// <summary>"ais" or "radar" - tells JS which icon family to use
    /// and whether to render buddy / threat overlays (radar targets
    /// drop those).</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "ais";
    /// <summary>"SART" / "MOB" / "EPIRB" or null. AisSart classification
    /// lifted out of JS.</summary>
    [JsonPropertyName("sartCategory")] public string? SartCategory { get; set; }
    /// <summary>"sail" / "fish" / "commercial" / "service" / null. Drives
    /// the JS-side glyph variant inside the AIS palette.</summary>
    [JsonPropertyName("glyphCategory")] public string? GlyphCategory { get; set; }
    [JsonPropertyName("shipColor")] public string? ShipColor { get; set; }
    [JsonPropertyName("cpaNm")] public double? CpaNm { get; set; }
    [JsonPropertyName("tcpaMin")] public double? TcpaMin { get; set; }
    /// <summary>"none" / "warning" / "danger" - precomputed threat
    /// band for the chart chip. JS used to redo this thresholding
    /// inline; lifting it up means CpaTests.ClassifyThreat is the
    /// single source of truth.</summary>
    [JsonPropertyName("cpaThreat")] public string CpaThreat { get; set; } = "none";
    [JsonPropertyName("colregsLabel")] public string? ColregsLabel { get; set; }
    [JsonPropertyName("colregsRole")] public string? ColregsRole { get; set; }
    /// <summary>Seconds since the last delta touched this vessel. JS
    /// uses it to fade stale markers (>30 s).</summary>
    [JsonPropertyName("ageSec")] public int AgeSec { get; set; }

    /// <summary>COG-vector endpoint precomputed from current lat / lon +
    /// CourseOverGround + SpeedOverGround * AisCogVectorMinutes. Lifted
    /// out of JS so the per-tick vectorEnd() / destPoint() trig (two
    /// asin/atan2 + four sin/cos per vessel) runs once in WASM per
    /// snapshot push instead of 200+ times per tick in the JS hot
    /// path. Null when SOG is below the 0.1 m/s stationary threshold
    /// or COG / SOG are missing - JS treats null as "don't draw the
    /// vector".</summary>
    [JsonPropertyName("vectorEndLat")] public double? VectorEndLat { get; set; }
    [JsonPropertyName("vectorEndLon")] public double? VectorEndLon { get; set; }

    /// <summary>CPA endpoint precomputed for the vessel's projected
    /// position at TCPA. Only populated when the CPA threat band is
    /// Warning or Danger (the only states where the JS draws the
    /// crossing-situation line); null otherwise. Same WASM-side
    /// destPoint() that produced VectorEndLat / Lon.</summary>
    [JsonPropertyName("cpaPointLat")] public double? CpaPointLat { get; set; }
    [JsonPropertyName("cpaPointLon")] public double? CpaPointLon { get; set; }

    /// <summary>
    /// Fresh trail coords for this vessel as <c>[[lat,lon], ...]</c>,
    /// or <c>null</c> when the trail didn't change since the last
    /// push (JS leaves the existing polyline alone) or is too short
    /// to draw. Owned C# side by <see cref="AisTrailBuffer"/>; the
    /// JS layer used to keep a per-vessel rolling window of its own
    /// (<c>aisLayer.aisTrailHistory</c>) and rebuild the coord array
    /// every tick - that state + the per-tick allocator pressure is
    /// gone now.
    ///
    /// <para>Wire-format: nested arrays (one per point) rather than a
    /// flat <c>Float64Array</c>. Easier to read JS-side; the wire cost
    /// difference is small in practice because most vessels are
    /// stationary between deltas and thus have <c>null</c> here on
    /// most ticks.</para>
    /// </summary>
    [JsonPropertyName("trail")] public double[][]? Trail { get; set; }
}
