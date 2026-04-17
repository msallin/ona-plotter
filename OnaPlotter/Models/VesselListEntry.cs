namespace OnaPlotter.Models;

/// <summary>
/// Snapshot of an AIS vessel plus ownship-relative figures, built once per
/// Layers-panel render. Sorted by TCPA so the most pressing threat is at the
/// top; vessels without a valid CPA fall to the bottom.
/// </summary>
public sealed record VesselListEntry(
    string Context,
    string DisplayName,
    double? CpaNm,
    double? TcpaMin,
    double? DistanceNm,
    double? BearingDeg,
    double? SogKn,
    string? ShipType,
    bool IsBuddy);
