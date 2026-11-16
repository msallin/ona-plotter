using System.Globalization;
using OnaPlotter.Models;
using OnaPlotter.Services.Js;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Map;

/// <summary>
/// Builds the rendered AIS-target state from an <see cref="AisStore"/>
/// snapshot and pushes what changed through <see cref="IMapAisJs"/>.
/// Owns every presentation decision so the JS layer is a renderer:
/// <list type="bullet">
/// <item><description>CPA + COLREGS classification (<c>Utilities.Cpa</c>,
/// <c>Utilities.Colregs</c>), dead-reckoned like the alarm rule.</description></item>
/// <item><description>Colour roles, staleness opacity, label visibility,
/// CPA overlay mode, name fallbacks, popup range / bearing
/// (<see cref="AisVesselPresentation"/>).</description></item>
/// <item><description>Harbor-mode + position filter via
/// <c>HarborAisFilter.Apply</c>.</description></item>
/// </list>
///
/// <para>Wire protocol: the first push (and any push after a failure or
/// a renderer that lost its state) is a full snapshot. After that each
/// push is a versioned delta with only the vessels whose rendered state
/// changed, identity fields only when they changed, trail points
/// appended / trimmed since the last push, and removed contexts. See
/// <see cref="AisUpdatePayload"/>.</para>
///
/// Lifecycle: instantiated by Map.razor once the JS module reference is
/// available. The page kicks <see cref="PushAsync"/> on a 3 s timer;
/// tests drive it directly.
///
/// Threading: Blazor WASM is single-threaded; assumes every call runs on
/// the renderer's synchronisation context.
/// </summary>
public sealed class AisLayerController
{
    private readonly IMapAisJs _aisJs;
    private readonly AisStore _aisStore;
    private readonly IMooredVesselTracker _mooredTracker;
    private readonly IAppSettings _settings;
    private readonly TimeProvider _time;
    private readonly AisTrailBuffer _trailBuffer = new();

    /// <summary>What the renderer holds per context, as of the last
    /// accepted push. Diffed against each new build.</summary>
    private Dictionary<string, SentVessel> _sent = new(StringComparer.Ordinal);

    /// <param name="Payload">Last sent dynamic state (Static and trail
    /// fields cleared so record equality compares only the rest).</param>
    /// <param name="Static">Last sent identity fields.</param>
    /// <param name="StaticKey">Inputs <paramref name="Static"/> was built
    /// from; lets an unchanged vessel skip rebuilding its strings.</param>
    private sealed record SentVessel(AisVesselPayload Payload, AisVesselStatic Static, StaticKey StaticKey);

    /// <summary>Inputs to <see cref="AisVesselStatic"/>.</summary>
    private readonly record struct StaticKey(
        string? Name, string? Mmsi, string? Callsign, string? ShipType,
        double? LoaM, double? BeamM, bool Buddy, bool Radar, string Context);

    private int _seq;
    /// <summary>True until the renderer has accepted a full snapshot, and
    /// again after any failed or refused push.</summary>
    private bool _needsFull = true;
    /// <summary>A push is awaiting the renderer. A timer tick that lands
    /// meanwhile is skipped: a second delta built on the same base would
    /// be refused and force a full resend.</summary>
    private bool _inFlight;

    /// <summary>Inputs of the last successful push; when all are unchanged
    /// and <see cref="MaxSkipInterval"/> hasn't elapsed the tick is a
    /// no-op without building anything.</summary>
    private int _lastPushedVersion = -1;
    private double? _lastOwnLat, _lastOwnLon, _lastOwnCog, _lastOwnSog;
    private bool _lastHarborMode;
    private bool _lastAnchorActive;
    private double? _lastAnchorMaxRadius;
    private double _lastInactiveMinutes = double.NaN;
    private DateTime _lastPushedAtUtc = DateTime.MinValue;

    /// <summary>Maximum quiet window before the skip optimisation gives
    /// up and rebuilds anyway, so time-driven state (moored-vessel dwell
    /// expiry, staleness fade, dead-reckoned CPA) still surfaces.</summary>
    internal static readonly TimeSpan MaxSkipInterval = TimeSpan.FromSeconds(5);

    public AisLayerController(
        IMapAisJs aisJs,
        AisStore aisStore,
        IMooredVesselTracker mooredTracker,
        IAppSettings settings,
        TimeProvider time)
    {
        _aisJs = aisJs ?? throw new ArgumentNullException(nameof(aisJs));
        _aisStore = aisStore ?? throw new ArgumentNullException(nameof(aisStore));
        _mooredTracker = mooredTracker ?? throw new ArgumentNullException(nameof(mooredTracker));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// Builds and pushes the current AIS state. A JS-side failure is
    /// caught and logged so a hot-path bug doesn't take down the Blazor
    /// render pass; the next tick resends a full snapshot.
    /// </summary>
    public async Task PushAsync(NavigationData ownship)
    {
        if (ownship is null || _inFlight) return;
        int storeVersion = _aisStore.Version;
        bool harbor = _settings.HarborMode;
        double inactiveMinutes = _settings.AisInactiveMinutes;
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        if (!_needsFull
            && storeVersion == _lastPushedVersion
            && ownship.Latitude == _lastOwnLat
            && ownship.Longitude == _lastOwnLon
            && ownship.CourseOverGroundTrueResolved == _lastOwnCog
            && ownship.SpeedOverGround == _lastOwnSog
            && harbor == _lastHarborMode
            && ownship.AnchorActive == _lastAnchorActive
            && ownship.AnchorMaxRadius == _lastAnchorMaxRadius
            && inactiveMinutes.Equals(_lastInactiveMinutes)
            && (nowUtc - _lastPushedAtUtc) < MaxSkipInterval)
        {
            return;
        }

        _inFlight = true;
        try
        {
            bool accepted = await BuildAndSendAsync(ownship, nowUtc, full: _needsFull);
            if (!accepted && !_needsFull)
            {
                // The renderer lost the base this delta applies to (map
                // re-init); answer with a full snapshot straight away.
                accepted = await BuildAndSendAsync(ownship, nowUtc, full: true);
            }
            _needsFull = !accepted;
            if (!accepted) return;

            _lastPushedVersion = storeVersion;
            _lastOwnLat = ownship.Latitude;
            _lastOwnLon = ownship.Longitude;
            _lastOwnCog = ownship.CourseOverGroundTrueResolved;
            _lastOwnSog = ownship.SpeedOverGround;
            _lastHarborMode = harbor;
            _lastAnchorActive = ownship.AnchorActive;
            _lastAnchorMaxRadius = ownship.AnchorMaxRadius;
            _lastInactiveMinutes = inactiveMinutes;
            _lastPushedAtUtc = nowUtc;
        }
        catch (Exception ex)
        {
            // Topmost handler for a per-tick push: a stale Leaflet handle
            // or JS regression must not reach Blazor's error UI and empty
            // the chart. What the renderer holds is now unknown, so the
            // next tick resends everything. Console.WriteLine (not
            // Console.Error) so errorRelayBoot.js doesn't relay this
            // handled-and-recovered case.
            _needsFull = true;
            Console.WriteLine($"[interop] PushAisTargets: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _inFlight = false;
        }
    }

    /// <summary>Per-tick own-ship state shared by every per-vessel
    /// computation.</summary>
    private readonly record struct OwnContext(
        Cpa.OwnSnapshot Snap,
        double Lat, double Lon, double Cog, double Sog,
        Colregs.VesselType OwnType);

    /// <summary>Per-vessel CPA + COLREGS bundle. Default (all null, threat
    /// None) is "no closing encounter".</summary>
    /// <param name="FromLat">Latitude the CPA was computed from: the
    /// target dead-reckoned to now (see <see cref="AisDeadReckoning"/>),
    /// so the CPA-point overlay starts from the same place.</param>
    private readonly record struct VesselThreat(
        double? CpaNm, double? TcpaMin,
        Cpa.Threat Threat,
        Colregs.Result? Colregs,
        double FromLat = 0, double FromLon = 0);

    /// <summary>Per-tick inputs that are the same for every vessel.</summary>
    private readonly record struct TickContext(
        OwnContext? Own, double? OwnLat, double? OwnLon, DateTime NowUtc,
        double CpaAlarmEff, double TcpaAlarmEff,
        double CpaAwarenessEff, double TcpaAwarenessEff,
        double CogVectorMinutes, double InactiveSeconds);

    /// <summary>Builds this tick's state, diffs it against
    /// <see cref="_sent"/> (unless <paramref name="full"/>), and sends.
    /// Returns whether the renderer accepted it; on acceptance
    /// <see cref="_sent"/> becomes the new state.</summary>
    private async Task<bool> BuildAndSendAsync(NavigationData ownship, DateTime nowUtc, bool full)
    {
        var tick = BuildTickContext(ownship, nowUtc);
        var visible = HarborAisFilter.Apply(_aisStore.GetVessels(), _mooredTracker, _settings.HarborMode, nowUtc);

        var next = new Dictionary<string, SentVessel>(visible.Count, StringComparer.Ordinal);
        var changed = new List<AisVesselPayload>(full ? visible.Count : 8);
        foreach (var v in visible)
        {
            if (!TryGuardFiniteInputs(v, out double vLat, out double vLon))
            {
                LogSkippedFiniteCheck(v, nowUtc);
                continue;
            }
            // Duplicate contexts can't reach the wire twice.
            if (next.ContainsKey(v.Context)) continue;
            try
            {
                var threat = ComputeVesselThreat(in tick, v);
                var payload = BuildPayload(v, vLat, vLon, in threat, in tick);
                _sent.TryGetValue(v.Context, out var prev);
                var key = StaticKeyFor(v);
                var stat = prev is not null && prev.StaticKey == key ? prev.Static : BuildStatic(v, key);

                // Trail bookkeeping runs every tick so expiry is tracked
                // even for vessels with nothing else to send.
                _trailBuffer.Push(v.Context, vLat, vLon, nowUtc);
                next[v.Context] = new SentVessel(payload, stat, key);

                if (full || prev is null)
                {
                    var trail = _trailBuffer.GetCoords(v.Context);
                    _trailBuffer.MarkSynced(v.Context);
                    changed.Add(payload with { Static = stat, Trail = trail });
                    continue;
                }
                var delta = _trailBuffer.ConsumeDelta(v.Context);
                bool staticChanged = !ReferenceEquals(stat, prev.Static) && stat != prev.Static;
                if (staticChanged || payload != prev.Payload || !delta.IsEmpty)
                {
                    changed.Add(payload with
                    {
                        Static = staticChanged ? stat : null,
                        TrailAppend = delta.Append,
                        TrailTrim = delta.Trim,
                    });
                }
            }
            catch (Exception ex)
            {
                // Per-vessel firewall: a corrupt record or math edge case
                // must not poison the rest of the snapshot. Includes the
                // inputs so a recurring offender is reconstructable.
                Console.WriteLine(
                    $"[ais] BuildSnapshot skipped {v.Context}: {ex.GetType().Name}: {ex.Message}" +
                    $" (lat={v.Latitude} lon={v.Longitude} cog={v.CourseOverGround} sog={v.SpeedOverGround})");
            }
        }
        _trailBuffer.EndSweep();

        string[] removed = full
            ? []
            : _sent.Keys.Where(ctx => !next.ContainsKey(ctx)).ToArray();

        if (!full && changed.Count == 0 && removed.Length == 0)
        {
            _sent = next;
            return true;
        }

        int seq = _seq + 1;
        var update = new AisUpdatePayload(
            Seq: seq,
            BaseSeq: full ? AisUpdatePayload.FullSnapshot : _seq,
            NowUtc: nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            Vessels: changed.ToArray(),
            Removed: removed);
        bool accepted = await _aisJs.UpdateAisTargetsAsync(update);
        if (accepted)
        {
            _seq = seq;
            _sent = next;
        }
        return accepted;
    }

    private TickContext BuildTickContext(NavigationData ownship, DateTime nowUtc)
    {
        // Two-tier CPA thresholds, anchor-narrowed with the same helper
        // as the alarm rule so the audible alarm and the chart agree.
        return new TickContext(
            Own: TryBuildOwnContext(ownship, _settings.OwnVesselType),
            OwnLat: ownship.Latitude is double la && double.IsFinite(la) ? la : null,
            OwnLon: ownship.Longitude is double lo && double.IsFinite(lo) ? lo : null,
            NowUtc: nowUtc,
            CpaAlarmEff: Cpa.EffectiveCpaRadiusNm(_settings.CpaAlarmNm, ownship.AnchorActive, ownship.AnchorMaxRadius),
            TcpaAlarmEff: _settings.TcpaAlarmMin,
            CpaAwarenessEff: Cpa.EffectiveCpaRadiusNm(_settings.CpaAwarenessNm, ownship.AnchorActive, ownship.AnchorMaxRadius),
            TcpaAwarenessEff: _settings.TcpaAwarenessMin,
            CogVectorMinutes: _settings.AisCogVectorMinutes,
            InactiveSeconds: _settings.AisInactiveMinutes * 60.0);
    }

    /// <summary>Returns true with the unwrapped finite lat/lon when the
    /// vessel's position is publishable. COG / SOG may be null (radar
    /// ARPA pre-tracking) but not non-finite, which would trip JSON
    /// serialisation of the whole push.</summary>
    private static bool TryGuardFiniteInputs(AisVessel v, out double lat, out double lon)
    {
        lat = lon = double.NaN;
        if (v.Latitude is not double vLat || !double.IsFinite(vLat)) return false;
        if (v.Longitude is not double vLon || !double.IsFinite(vLon)) return false;
        if (v.CourseOverGround is double vCog && !double.IsFinite(vCog)) return false;
        if (v.SpeedOverGround is double vSog && !double.IsFinite(vSog)) return false;
        lat = vLat;
        lon = vLon;
        return true;
    }

    /// <summary>One-per-minute cap on finite-check skip logs so a vessel
    /// spamming non-finite deltas can't flood the log.</summary>
    private DateTime _lastSkipFiniteLogUtc = DateTime.MinValue;
    private static readonly TimeSpan SkipLogInterval = TimeSpan.FromMinutes(1);

    private void LogSkippedFiniteCheck(AisVessel v, DateTime nowUtc)
    {
        if ((nowUtc - _lastSkipFiniteLogUtc) < SkipLogInterval) return;
        _lastSkipFiniteLogUtc = nowUtc;
        Console.WriteLine(
            $"[ais] BuildSnapshot finite-check skipped {v.Context}:" +
            $" lat={v.Latitude} lon={v.Longitude} cog={v.CourseOverGround} sog={v.SpeedOverGround}" +
            $" (rate-limited to one log/min)");
    }

    private static OwnContext? TryBuildOwnContext(NavigationData ownship, string ownVesselType)
    {
        // True-north COG: AIS target COG is true, so the own side of the
        // CPA / COLREGS geometry must be too, whatever the HUD displays.
        double? ownCog = ownship.CourseOverGroundTrueResolved;
        var snap = Cpa.PrecomputeOwn(
            ownship.Latitude ?? double.NaN, ownship.Longitude ?? double.NaN,
            ownCog, ownship.SpeedOverGround);
        if (snap is null) return null;
        var ownType = ownVesselType == "sail"
            ? Colregs.VesselType.Sail
            : Colregs.VesselType.Power;
        return new OwnContext(
            snap.Value,
            ownship.Latitude!.Value, ownship.Longitude!.Value,
            ownCog!.Value, ownship.SpeedOverGround!.Value,
            ownType);
    }

    /// <summary>CPA + threat band for a single target. COLREGS runs only
    /// for Warning / Danger targets: the rules apply to closing
    /// encounters, and skipping the rest saves most Classify calls in a
    /// busy harbour.</summary>
    private static VesselThreat ComputeVesselThreat(in TickContext tick, AisVessel v)
    {
        if (tick.Own is not { } own
            || v.CourseOverGround is null || v.SpeedOverGround is null)
        {
            return default;
        }
        // Same starting point as CpaAlarmRule: the target dead-reckoned
        // to now. A fix too old to extrapolate gets no CPA, matching the
        // alarm rule so chip and klaxon agree.
        if (AisDeadReckoning.PositionAt(v, tick.NowUtc) is not { } tgt) return default;
        var cpa = Cpa.Compute(own.Snap, tgt.Latitude, tgt.Longitude, v.CourseOverGround, v.SpeedOverGround);
        if (cpa is not { } c) return default;

        var threat = Cpa.ClassifyThreat(
            c.CpaNm, c.TcpaMin,
            tick.CpaAlarmEff, tick.TcpaAlarmEff,
            tick.CpaAwarenessEff, tick.TcpaAwarenessEff,
            v.IsBuddy);

        Colregs.Result? colregs = null;
        if (threat != Cpa.Threat.None)
        {
            // Own type from the helm setting, target from
            // design.aisShipType; mixed propulsion overrides the role by
            // Rule 18.
            colregs = Colregs.Classify(
                own.Lat, own.Lon, own.Cog, own.Sog,
                tgt.Latitude, tgt.Longitude,
                v.CourseOverGround.Value, v.SpeedOverGround.Value,
                own.OwnType, Colregs.FromAisShipType(v.ShipType));
        }
        return new VesselThreat(c.CpaNm, c.TcpaMin, threat, colregs, tgt.Latitude, tgt.Longitude);
    }

    private static StaticKey StaticKeyFor(AisVessel v) => new(
        v.Name, v.Mmsi, v.Callsign, v.ShipType,
        v.LengthOverallMeters, v.BeamMeters,
        v.IsBuddy, AisVesselPresentation.IsRadar(v), v.Context);

    private static AisVesselStatic BuildStatic(AisVessel v, StaticKey key) => new()
    {
        Name = v.Name,
        Mmsi = v.Mmsi,
        Buddy = key.Buddy,
        Radar = key.Radar,
        SartCategory = AisSart.CategoryFromAny(v.Mmsi, v.Context),
        GlyphCategory = key.Radar ? null : AisPalette.ShipTypeCategory(v.ShipType),
        ShipColor = AisPalette.ShipTypeColor(v.ShipType),
        DisplayName = AisVesselPresentation.DisplayName(v.Name, v.Mmsi, key.Buddy),
        Title = AisVesselPresentation.Title(v.Name, v.Callsign, v.Mmsi, key.Buddy),
        Subtitle = AisVesselPresentation.Subtitle(v.Mmsi, v.Callsign, v.ShipType,
            v.LengthOverallMeters, v.BeamMeters),
    };

    /// <summary>Resolves every rendered field of one vessel. Static and
    /// trail fields are left unset; the diff fills them in.</summary>
    private static AisVesselPayload BuildPayload(
        AisVessel v, double vLat, double vLon, in VesselThreat threat, in TickContext tick)
    {
        bool radar = AisVesselPresentation.IsRadar(v);
        bool sart = AisSart.CategoryFromAny(v.Mmsi, v.Context) is not null;
        bool alarm = threat.Threat == Cpa.Threat.Alarm;
        double ageSec = (tick.NowUtc - v.LastAisSeen).TotalSeconds;
        string? displayName = AisVesselPresentation.DisplayName(v.Name, v.Mmsi, v.IsBuddy);

        var vec = GeoMath.VectorEnd(vLat, vLon, v.CourseOverGround, v.SpeedOverGround, tick.CogVectorMinutes);

        // CPA endpoints: the target's projected position at TCPA, and own
        // ship's at the same instant; the overlay draws the segment
        // between them as the actual closest approach.
        double? cpaLat = null, cpaLon = null, ownCpaLat = null, ownCpaLon = null;
        if (threat.Threat != Cpa.Threat.None
            && threat.TcpaMin is double tcpaMin
            && v.CourseOverGround is double cog
            && v.SpeedOverGround is double sog)
        {
            (cpaLat, cpaLon) = GeoMath.DestPoint(threat.FromLat, threat.FromLon, cog, sog * tcpaMin * 60.0);
            if (tick.Own is { } own)
            {
                (ownCpaLat, ownCpaLon) = GeoMath.DestPoint(own.Lat, own.Lon, own.Cog, own.Sog * tcpaMin * 60.0);
            }
        }
        var overlay = AisVesselPresentation.CpaOverlay(threat.Threat, threat.TcpaMin, cpaLat is not null);
        bool closing = threat.TcpaMin is > 0;

        var colregs = threat.Colregs is { Category: not Colregs.Category.Indeterminate } r ? r : (Colregs.Result?)null;

        double? rangeNm = null, bearingDeg = null;
        if (tick.OwnLat is double oLat && tick.OwnLon is double oLon)
        {
            rangeNm = AisVesselPresentation.Round(
                GeoMath.HaversineMeters(oLat, oLon, vLat, vLon) / Format.MetersPerNm, 2);
            bearingDeg = AisVesselPresentation.Round(GeoMath.BearingDeg(oLat, oLon, vLat, vLon), 0) % 360.0;
        }

        return new AisVesselPayload
        {
            Context = v.Context,
            Lat = vLat,
            Lon = vLon,
            RotationRad = AisVesselPresentation.RotationRad(sart, v.CourseOverGround, v.Heading),
            MarkerTone = AisVesselPresentation.MarkerTone(radar, v.IsBuddy, alarm),
            VectorTone = AisVesselPresentation.VectorTone(radar, v.IsBuddy),
            Pulse = alarm && !sart,
            // SART beacons and buddies keep full opacity: life-safety
            // transmitters drop out briefly and still matter, and the
            // "where's my friend" workflow tolerates lateness.
            Opacity = sart || v.IsBuddy ? null : StalenessOpacity.Compute(ageSec, tick.InactiveSeconds),
            LabelVisible = AisVesselPresentation.LabelVisible(
                displayName, ageSec, tick.InactiveSeconds, sart, v.IsBuddy),
            VectorEndLat = vec?.Lat,
            VectorEndLon = vec?.Lon,
            CpaThreat = ThreatToWireString(threat.Threat),
            CpaOverlay = overlay,
            CpaNm = closing ? AisVesselPresentation.Round(threat.CpaNm, 2) : null,
            TcpaMin = closing ? AisVesselPresentation.Round(threat.TcpaMin, 0) : null,
            CpaPointLat = overlay == AisCpaOverlay.None ? null : cpaLat,
            CpaPointLon = overlay == AisCpaOverlay.None ? null : cpaLon,
            OwnCpaPointLat = overlay == AisCpaOverlay.None ? null : ownCpaLat,
            OwnCpaPointLon = overlay == AisCpaOverlay.None ? null : ownCpaLon,
            ColregsLabel = colregs is { } cl ? Colregs.ShortLabel(cl.Category) : null,
            ColregsRole = colregs is { } cr ? Colregs.RoleLabel(cr.Role) : null,
            ColregsGiveWay = colregs is { Role: Colregs.Role.GiveWay },
            Snoozable = !v.IsBuddy && threat.Threat != Cpa.Threat.None,
            SogKn = AisVesselPresentation.Round(v.SpeedOverGround * Format.MsToKnots, 1),
            CogDeg = AisVesselPresentation.WholeDegrees(v.CourseOverGround),
            HeadingDeg = AisVesselPresentation.WholeDegrees(v.Heading),
            RangeNm = rangeNm,
            BearingDeg = bearingDeg,
            LastAisSeen = v.LastAisSeen.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Wire string for the threat band. Pinned in
    /// <c>AisLayerControllerTests</c>; <c>aisLayer.js</c> reads
    /// <c>'alarm'</c> for the popup's danger styling.</summary>
    internal static string ThreatToWireString(Cpa.Threat t) => t switch
    {
        Cpa.Threat.Alarm => "alarm",
        Cpa.Threat.Awareness => "awareness",
        _ => "none",
    };
}
