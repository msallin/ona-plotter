using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Map;

/// <summary>
/// Builds the Layers-panel vessel list: every AIS target with range,
/// bearing, CPA / TCPA and COLREGS, sorted by TCPA (closing targets
/// first) then distance.
///
/// <para>CPA starts from the target dead-reckoned to now, exactly as
/// <c>CpaAlarmRule</c> and <see cref="AisPushService"/> do, so the list,
/// the chart chip and the alarm agree; a fix too old to extrapolate gets
/// no CPA. COLREGS is classified (type-aware) only inside the threat
/// band.</para>
///
/// <para>Cached: the panel binding runs on every page render, so the list
/// is rebuilt only when the AIS store changed, own ship moved more than
/// ~50 m / 1 deg / 0.1 m/s, a threshold changed, or the 3 s
/// dead-reckoning bucket rolled over. Sensor noise on own position then
/// doesn't rebuild a 200-vessel list.</para>
/// </summary>
public sealed class VesselListBuilder
{
    /// <summary>Dead-reckoned CPA drifts with time even when no input
    /// changes; the list follows it at the AIS push cadence.</summary>
    internal static readonly TimeSpan TimeBucket = TimeSpan.FromSeconds(3);

    private IReadOnlyList<VesselListEntry>? _cached;
    private CacheKey _cachedKey;

    /// <summary>Everything a build reads, quantised. Own position to
    /// 0.0005 deg (~55 m north-south), COG to whole degrees, SOG to
    /// 0.1 m/s.</summary>
    internal readonly record struct CacheKey(
        int AisVersion,
        long OwnLat, long OwnLon, long OwnCog, long OwnSog,
        bool AnchorActive, double? AnchorMaxRadius,
        double CpaAlarmNm, double TcpaAlarmMin,
        double CpaAwarenessNm, double TcpaAwarenessMin,
        string OwnVesselType, long TimeBucket);

    /// <summary>Returns the cached list when nothing it depends on moved
    /// past its quantum, else rebuilds.</summary>
    public IReadOnlyList<VesselListEntry> Get(
        AisStore store, NavigationData own, IAppSettings settings, DateTime nowUtc)
    {
        var key = KeyFor(store.Version, own, settings, nowUtc);
        if (_cached is not null && key == _cachedKey) return _cached;
        _cached = Build(store.GetVessels(), own, settings, nowUtc);
        _cachedKey = key;
        return _cached;
    }

    internal static CacheKey KeyFor(int aisVersion, NavigationData own, IAppSettings settings, DateTime nowUtc) => new(
        aisVersion,
        Quantise(own.Latitude, 2000), Quantise(own.Longitude, 2000),
        Quantise(own.CourseOverGroundTrueResolved, 180.0 / Math.PI),
        Quantise(own.SpeedOverGround, 10),
        own.AnchorActive, own.AnchorMaxRadius,
        settings.CpaAlarmNm, settings.TcpaAlarmMin,
        settings.CpaAwarenessNm, settings.TcpaAwarenessMin,
        settings.OwnVesselType,
        nowUtc.Ticks / TimeBucket.Ticks);

    private static long Quantise(double? v, double scale) =>
        v is double d && double.IsFinite(d) ? (long)Math.Round(d * scale) : long.MinValue;

    internal static List<VesselListEntry> Build(
        IReadOnlyList<AisVessel> vessels, NavigationData own, IAppSettings settings, DateTime nowUtc)
    {
        var entries = new List<VesselListEntry>(vessels.Count);
        if (vessels.Count == 0) return entries;

        double? ownLat = own.Latitude, ownLon = own.Longitude;
        // True-north COG: AIS COG is true, so the own side must be too,
        // independent of the magnetic display toggle.
        double? ownCog = own.CourseOverGroundTrueResolved;
        double? ownSog = own.SpeedOverGround;
        var ownSnap = ownLat is double la && ownLon is double lo
            ? Cpa.PrecomputeOwn(la, lo, ownCog, ownSog)
            : null;
        double cpaAlarmEff = Cpa.EffectiveCpaRadiusNm(settings.CpaAlarmNm, own.AnchorActive, own.AnchorMaxRadius);
        double cpaAwarenessEff = Cpa.EffectiveCpaRadiusNm(settings.CpaAwarenessNm, own.AnchorActive, own.AnchorMaxRadius);
        var ownType = settings.OwnVesselType == "sail" ? Colregs.VesselType.Sail : Colregs.VesselType.Power;

        foreach (var v in vessels)
        {
            if (v.Latitude is not double vLat || v.Longitude is not double vLon) continue;

            double? distNm = null, brgDeg = null;
            if (ownLat is double oLat && ownLon is double oLon)
            {
                distNm = GeoMath.HaversineMeters(oLat, oLon, vLat, vLon) / Format.MetersPerNm;
                brgDeg = GeoMath.BearingDeg(oLat, oLon, vLat, vLon);
            }

            Cpa.Result? cpa = null;
            Colregs.Result? colregs = null;
            if (ownSnap is { } snap && AisDeadReckoning.PositionAt(v, nowUtc) is { } tgt)
            {
                cpa = Cpa.Compute(snap, tgt.Latitude, tgt.Longitude, v.CourseOverGround, v.SpeedOverGround);
                if (cpa is { } c
                    && v.CourseOverGround is double vCog && v.SpeedOverGround is double vSog
                    && Cpa.ClassifyThreat(
                        c.CpaNm, c.TcpaMin,
                        cpaAlarmEff, settings.TcpaAlarmMin,
                        cpaAwarenessEff, settings.TcpaAwarenessMin,
                        v.IsBuddy) != Cpa.Threat.None)
                {
                    colregs = Colregs.Classify(
                        ownLat!.Value, ownLon!.Value, ownCog!.Value, ownSog!.Value,
                        tgt.Latitude, tgt.Longitude, vCog, vSog,
                        ownType, Colregs.FromAisShipType(v.ShipType));
                }
            }

            entries.Add(new VesselListEntry(
                Context: v.Context,
                DisplayName: v.Name ?? v.Callsign ?? (v.Mmsi is not null ? $"MMSI {v.Mmsi}" : v.Context),
                Mmsi: v.Mmsi,
                CpaNm: cpa?.CpaNm,
                TcpaMin: cpa?.TcpaMin,
                DistanceNm: distNm,
                BearingDeg: brgDeg,
                SogKn: v.SpeedOverGround * Format.MsToKnots,
                ShipType: v.ShipType,
                IsBuddy: v.IsBuddy,
                ColregsLabel: colregs is { Category: var cat and not Colregs.Category.Indeterminate }
                    ? Colregs.ShortLabel(cat) : null,
                ColregsRole: colregs is { Role: var role and not Colregs.Role.None }
                    ? Colregs.RoleLabel(role) : null,
                LengthOverallMeters: v.LengthOverallMeters,
                BeamMeters: v.BeamMeters));
        }

        // Closing targets by TCPA; the rest by distance.
        entries.Sort((a, b) =>
        {
            bool aHas = a.TcpaMin is not null;
            bool bHas = b.TcpaMin is not null;
            if (aHas && bHas) return a.TcpaMin!.Value.CompareTo(b.TcpaMin!.Value);
            if (aHas) return -1;
            if (bHas) return 1;
            return (a.DistanceNm ?? double.MaxValue).CompareTo(b.DistanceNm ?? double.MaxValue);
        });
        return entries;
    }
}
