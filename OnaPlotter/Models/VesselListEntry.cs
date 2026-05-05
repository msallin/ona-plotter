namespace OnaPlotter.Models;

/// <summary>
/// Snapshot of an AIS vessel plus ownship-relative figures, built once per
/// Layers-panel render. Sorted by TCPA so the most pressing threat is at the
/// top; vessels without a valid CPA fall to the bottom.
/// </summary>
public sealed record VesselListEntry(
    string Context,
    string DisplayName,
    string? Mmsi,
    double? CpaNm,
    double? TcpaMin,
    double? DistanceNm,
    double? BearingDeg,
    double? SogKn,
    string? ShipType,
    bool IsBuddy,
    string? ColregsLabel,
    string? ColregsRole,
    /// <summary>LOA in metres from <c>design.length.overall</c> (AIS
    /// Type 5 / 24 static). Null when the AIS source hasn't broadcast
    /// static -- common for class-B targets. Layers-panel row hides
    /// the dimensions chip entirely when both this and
    /// <see cref="BeamMeters"/> are null.</summary>
    double? LengthOverallMeters = null,
    /// <summary>Beam in metres from <c>design.beam</c>. Same null
    /// semantics as <see cref="LengthOverallMeters"/>.</summary>
    double? BeamMeters = null);
