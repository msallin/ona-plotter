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
    /// <summary>Seconds since the last AIS-evidence delta touched this vessel
    /// (position / motion / static-data). Computed from <c>AisVessel.LastAisSeen</c>
    /// rather than delta-arrival time so plugin chatter on derived paths
    /// doesn't keep ghost vessels looking fresh. JS uses it to fade stale
    /// markers and (past the helm-configured "AIS inactive" threshold)
    /// suppress their name label.</summary>
    [JsonPropertyName("ageSec")] public int AgeSec { get; set; }

    /// <summary>ISO 8601 (UTC, RFC 3339, e.g. <c>"2026-05-17T11:42:13Z"</c>)
    /// of the most recent AIS-evidence delta from <c>AisVessel.LastAisSeen</c>.
    /// The JS popup formats this as a 24h <c>hh:mm</c> with an inline "(N min
    /// ago)" tail so the helm sees both the exact instant and the elapsed
    /// time the marker is drawn against. Always populated.</summary>
    [JsonPropertyName("lastAisSeen")] public string? LastAisSeen { get; set; }

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
    /// Fresh trail coords for this vessel as a flat alternating
    /// <c>[lat0, lon0, lat1, lon1, ...]</c> array, or <c>null</c> when
    /// the trail didn't change since the last push (JS leaves the
    /// existing polyline alone) or is too short to draw. Owned C#-side
    /// by <see cref="AisTrailBuffer"/>.
    ///
    /// <para>When non-null, length is always even and at least 4 (two
    /// points minimum; JS suppresses degenerate single-vertex
    /// polylines). The JS side in <c>aisLayer.updateAisTrail</c>
    /// unpacks the pairs into Leaflet LatLng tuples in a single
    /// forward pass.</para>
    /// </summary>
    [JsonPropertyName("trail")] public double[]? Trail { get; set; }
}
