using OnaPlotter.Models;
using OnaPlotter.Services.Js;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Map;

/// <summary>
/// Builds the JS-side AIS-target payload from an <see cref="AisStore"/>
/// snapshot and pushes it through <see cref="IMapAisJs"/>. Owns the
/// pre-computation that lifts decision logic out of JS:
/// <list type="bullet">
/// <item><description>CPA + COLREGS classification (one source of
/// truth in <c>Utilities.Cpa</c> + <c>Utilities.Colregs</c>).</description></item>
/// <item><description>SART category, glyph category, ship-type colour,
/// CPA threat band, display name fallback.</description></item>
/// <item><description>Harbor-mode + position filter via
/// <c>HarborAisFilter.Apply</c>.</description></item>
/// </list>
///
/// Lifecycle: instantiated by Map.razor in <c>OnAfterRenderAsync</c>
/// once the JS module reference is available. The page kicks
/// <see cref="PushAsync"/> on a 3 s timer; tests drive it directly.
///
/// Threading: Blazor WASM is single-threaded; assumes every call runs
/// on the renderer's synchronisation context.
/// </summary>
public sealed class AisPushService
{
    private readonly IMapAisJs _aisJs;
    private readonly AisStore _aisStore;
    private readonly IMooredVesselTracker _mooredTracker;
    private readonly IAppSettings _settings;
    private readonly TimeProvider _time;
    /// <summary>Per-vessel trail sliding window. Owns what
    /// <c>aisLayer.aisTrailHistory</c> used to store JS-side. Lives
    /// across pushes so the buffer state survives the every-Nth-tick
    /// skip-optimisation; <see cref="FillPayload"/> emits coords on
    /// the per-vessel <see cref="AisVesselPayload.Trail"/> field only
    /// when the buffer reports a content change since the last
    /// emission, gated by <see cref="AisTrailBuffer.ConsumeDirty"/>.</summary>
    private readonly AisTrailBuffer _trailBuffer = new();

    /// <summary>Last AisStore.Version we pushed at. -1 forces an
    /// initial push so the JS layer is never starved on first tick.</summary>
    private int _lastPushedVersion = -1;
    /// <summary>Last own-ship geometry we pushed at. CPA / COLREGS /
    /// chip threat band are derived from these on every vessel, so a
    /// change in own-ship lat / lon / cog / sog must invalidate the
    /// skip even if the AisStore version is unchanged.</summary>
    private double? _lastOwnLat, _lastOwnLon, _lastOwnCog, _lastOwnSog;
    private bool _lastHarborMode;
    private bool _lastAnchorActive;
    private double? _lastAnchorMaxRadius;
    /// <summary>UTC instant of the most recent successful push. The
    /// skip-when-unchanged optimisation force-refreshes once every
    /// <see cref="MaxSkipInterval"/> regardless of version so time-
    /// driven state (moored-vessel SOG-dwell expiry, vessel-staleness
    /// ageSec fade in JS) still surfaces in the helm-visible UI.</summary>
    private DateTime _lastPushedAtUtc = DateTime.MinValue;
    /// <summary>Maximum quiet window before the skip optimisation
    /// gives up and pushes anyway. 5 s is long enough for the skip to
    /// elide steady-state ticks (3 s cadence -&gt; one push every other
    /// tick at worst when nothing changes) but short enough that
    /// time-based tracker state (moored-vessel dwell, JS opacity
    /// fade) refreshes promptly.</summary>
    internal static readonly TimeSpan MaxSkipInterval = TimeSpan.FromSeconds(5);

    public AisPushService(
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
    /// Builds + pushes the current AIS-target snapshot. JS-side
    /// regressions are caught and logged so a hot-path JS bug doesn't
    /// take down the Blazor render pass (we fire at 3 Hz; a toast per
    /// tick would be spam).
    /// <para>Skips the snapshot build + interop call entirely when
    /// nothing observable has changed since the last push: AisStore's
    /// monotonic version counter, own-ship lat / lon / cog / sog
    /// (drives CPA / COLREGS), HarborMode flag, and the anchor-active
    /// + anchor-max-radius pair (drive the effective CPA radius). 200-
    /// vessel harbour at 3 s cadence saves ~14 short-lived allocs per
    /// vessel per tick on idle ticks plus the JSInterop JSON cost.</para>
    /// </summary>
    public async Task PushAsync(NavigationData ownship)
    {
        if (ownship is null) return;
        int storeVersion = _aisStore.Version;
        bool harbor = _settings.HarborMode;
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        if (storeVersion == _lastPushedVersion
            && ownship.Latitude == _lastOwnLat
            && ownship.Longitude == _lastOwnLon
            && ownship.CourseOverGround == _lastOwnCog
            && ownship.SpeedOverGround == _lastOwnSog
            && harbor == _lastHarborMode
            && ownship.AnchorActive == _lastAnchorActive
            && ownship.AnchorMaxRadius == _lastAnchorMaxRadius
            && (nowUtc - _lastPushedAtUtc) < MaxSkipInterval)
        {
            // Nothing observable to push. Both AIS state and own-ship
            // geometry agree with the last push, and we're still
            // inside the time cap that catches tracker-state changes
            // (moored-vessel dwell expiring, ageSec fade) which the
            // version counter doesn't reflect.
            return;
        }

        // NB: cache commit is deliberately AFTER the work, not before.
        // If BuildSnapshot or the JS push throws, we need the next tick
        // to re-attempt with the same observable state - committing the
        // cache up-front would silently swallow the failed push and keep
        // the map showing stale data until something else changed
        // (own-ship moves, etc). The skip optimisation is purely an
        // idempotent fast-path, not a write-through commit.
        try
        {
            var jsVessels = BuildSnapshot(ownship);
            await _aisJs.UpdateAisTargetsAsync(jsVessels);
            _lastPushedVersion = storeVersion;
            _lastOwnLat = ownship.Latitude;
            _lastOwnLon = ownship.Longitude;
            _lastOwnCog = ownship.CourseOverGround;
            _lastOwnSog = ownship.SpeedOverGround;
            _lastHarborMode = harbor;
            _lastAnchorActive = ownship.AnchorActive;
            _lastAnchorMaxRadius = ownship.AnchorMaxRadius;
            _lastPushedAtUtc = nowUtc;
        }
        catch (Exception ex)
        {
            // Broad catch is intentional - this is the topmost handler
            // for a per-tick fire-and-forget push. Previously the catch
            // only matched JSException; an InvalidOperationException
            // bubbling up from a stale Leaflet handle (the radar-HUD
            // regression) propagated past here, tripped Blazor's
            // renderer error UI, and emptied the chart for the rest of
            // the session. Helm-feedback: "I enabled radar HUD ... I
            // dont see infos on the chart anymore". Catching at the
            // tick boundary keeps the rest of the page functional and
            // lets the next tick try again with fresh JS handles.
            // Console.WriteLine (not Console.Error) so errorRelayBoot.js
            // doesn't relay this handled-and-recovered case as an
            // unhandled error.
            Console.WriteLine($"[interop] PushAisTargets: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Pool of AisVesselPayload objects reused across pushes.
    /// Grows monotonically to the high-water-mark vessel count seen
    /// in any push. A 200-vessel busy harbour previously allocated
    /// 200 anonymous-type heap objects (~168 B each = ~33 KB) per
    /// push at 3 Hz = ~100 KB/sec of pure GC pressure on Pi 4. The
    /// pool drops that to a single AisVesselPayload[] array alloc
    /// per push (and zero pool allocs in steady state).
    ///
    /// <para>Concurrency: WASM is single-threaded; callers must not
    /// hold a payload reference past the next BuildSnapshot. The only
    /// caller, <see cref="PushAsync"/>, awaits UpdateAisTargetsAsync
    /// (which serialises synchronously inside Blazor's interop layer)
    /// before returning, so the pool is safe to mutate on the next
    /// tick.</para></summary>
    private readonly List<AisVesselPayload> _payloadPool = new();

    /// <summary>Per-tick own-ship state shared by every per-vessel
    /// computation in <see cref="BuildSnapshot"/>. Bundling these
    /// fields keeps the per-vessel helper signatures sane and pins
    /// "computed once per tick" in the type system.</summary>
    private readonly record struct OwnContext(
        Cpa.OwnSnapshot Snap,
        double Lat, double Lon, double Cog, double Sog,
        Colregs.VesselType OwnType);

    /// <summary>Per-vessel CPA + COLREGS bundle returned by
    /// <see cref="ComputeVesselThreat"/>. Default value (all null,
    /// threat None) is the "no closing encounter" outcome that
    /// applies whenever own state is incomplete or the vessel
    /// itself lacks COG/SOG.</summary>
    private readonly record struct VesselThreat(
        double? CpaNm, double? TcpaMin,
        Cpa.Threat Threat,
        string? ColregsLabel, string? ColregsRole);

    private AisVesselPayload[] BuildSnapshot(NavigationData ownship)
    {
        var vessels = _aisStore.GetVessels();
        bool harbor = _settings.HarborMode;
        var now = _time.GetUtcNow().UtcDateTime;

        // Harbor-mode + position filter delegated to HarborAisFilter so
        // the moored-classification + cleanup-on-vanished-context
        // contract is unit-testable in isolation. The tracker's
        // nav.state + SOG-dwell rules decide what counts as moored;
        // this caller just trusts the answer.
        var visible = HarborAisFilter.Apply(vessels, _mooredTracker, harbor, now);

        // Effective CPA radius for THIS snapshot (anchor-narrowing
        // when applicable). The visible guard-zone rings AND the
        // chip classifier MUST agree - using the underway threshold
        // here while the rings show the anchor-narrowed radius made
        // chips appear outside the rings ("how can this be?").
        // See OnaPlotter.Utilities.Cpa.EffectiveRadiusNm for the
        // single source of truth used by all three render paths
        // (rings, chips, alarm rule).
        double effectiveCpaRadiusNm = Cpa.EffectiveRadiusNm(
            _settings.CpaAlarmThreshold,
            ownship.AnchorActive,
            ownship.AnchorMaxRadius);
        double lookaheadMin = _settings.GuardZoneLookaheadMinutes;

        // Pre-compute own-ship sin/cos of COG ONCE here rather than on
        // every vessel inside the loop - the own-ship trig is
        // invariant across the per-target pass. Null when own-ship
        // inputs are missing/non-finite so the per-vessel helper
        // returns a default VesselThreat (no closing encounter).
        OwnContext? ownCtx = TryBuildOwnContext(ownship, _settings.OwnVesselType);

        // AIS COG-vector look-ahead in minutes. Hoist the settings
        // read out of the per-vessel loop; FillPayload uses it to
        // precompute VectorEndLat / Lon via GeoMath.VectorEnd so the
        // JS layer never runs destPoint() trig per vessel per tick.
        double aisCogVectorMinutes = _settings.AisCogVectorMinutes;

        // Hot path: 200+ vessels at ~3 Hz on a busy harbour push.
        // visible.Count is the upper bound; per-vessel guards below
        // (NaN/Infinity / try-catch) may skip entries, so we shrink
        // the result array at the end if the live count diverges.
        var result = new AisVesselPayload[visible.Count];
        int written = 0;
        for (int i = 0; i < visible.Count; i++)
        {
            var v = visible[i];

            // Defensive finite-check on every numeric input we feed into
            // CPA / haversine / COLREGS. AisStore.Apply already filters
            // null lat/lon, but a NaN slipping through (manually-crafted
            // delta, JSON parse glitch, future schema migration) used to
            // poison the whole snapshot - one NaN in Cpa.Compute returns
            // null, but the same NaN in the anonymous-payload write
            // would propagate to JSInterop and throw "Cannot serialize
            // NaN to JSON". The whole tick bailed and the map froze on
            // stale data. Skip the offender and keep the rest of the
            // snapshot.
            if (!TryGuardFiniteInputs(v, out double vLat, out double vLon))
            {
                LogSkippedFiniteCheck(v, now);
                continue;
            }

            try
            {
                var threat = ComputeVesselThreat(
                    in ownCtx, v, vLat, vLon,
                    effectiveCpaRadiusNm, lookaheadMin);

                AisVesselPayload p = AcquirePoolSlot(written);
                FillPayload(p, v, vLat, vLon, in threat, now, aisCogVectorMinutes, _trailBuffer);
                result[written++] = p;
            }
            catch (Exception ex)
            {
                // Per-vessel firewall. A bad vessel record (corrupt AIS
                // static, future enum value, math edge case) shouldn't
                // poison the whole snapshot - log it once and move on.
                // The outer try/catch in PushAsync would otherwise
                // discard every other vessel in the same tick. Includes
                // context + the inputs that fed the failing branch so
                // a recurring offender is reconstructable from the SK
                // server log alone.
                Console.WriteLine(
                    $"[ais] BuildSnapshot skipped {v.Context}: {ex.GetType().Name}: {ex.Message}" +
                    $" (lat={v.Latitude} lon={v.Longitude} cog={v.CourseOverGround} sog={v.SpeedOverGround})");
            }
        }
        // If any vessel was skipped, shrink to the live count so the
        // JS side doesn't see trailing default-initialised slots.
        AisVesselPayload[] snapshot = written != result.Length
            ? result.AsSpan(0, written).ToArray()
            : result;

        // Trail stale-sweep. Drop any context in the buffer that
        // isn't in the snapshot we're about to emit - matches what
        // the JS-side `for (const ctx of aisMarkers.keys()) ...
        // removeAisTrail(ctx)` loop did. Allocates a HashSet only
        // when there's actual stale content; in steady-state harbour
        // traffic the live set dominates the cached set.
        _trailBuffer.RetainOnly(snapshot.Select(p => p.Context!));
        return snapshot;
    }

    /// <summary>Returns true with the unwrapped finite lat/lon when
    /// the vessel's position is publishable, false (and the snapshot
    /// loop skips it) when any of lat / lon / cog / sog is null or
    /// non-finite. COG / SOG are allowed to be null (radar ARPA
    /// pre-tracking state); a non-finite value is rejected because it
    /// would propagate to the payload and trip JSON serialisation.</summary>
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

    /// <summary>One-per-minute cap on finite-check skip logs across
    /// the AisPushService instance. The Map page constructs a single
    /// instance, so in practice the cap covers every vessel the push
    /// pipeline touches - a vessel spamming non-finite deltas every
    /// tick can't flood the log, and a recurring offender still shows
    /// up once per minute. The field is per-instance, not static, so
    /// the cap doesn't leak between AisPushService instances in tests.</summary>
    private DateTime _lastSkipFiniteLogUtc = DateTime.MinValue;
    private static readonly TimeSpan SkipLogInterval = TimeSpan.FromMinutes(1);

    private void LogSkippedFiniteCheck(AisVessel v, DateTime nowUtc)
    {
        if ((nowUtc - _lastSkipFiniteLogUtc) < SkipLogInterval) return;
        _lastSkipFiniteLogUtc = nowUtc;
        // Includes the actual inputs so an operator reading the SK
        // log can reconstruct what the AIS payload looked like
        // without needing to reproduce the bug live.
        Console.WriteLine(
            $"[ais] BuildSnapshot finite-check skipped {v.Context}:" +
            $" lat={v.Latitude} lon={v.Longitude} cog={v.CourseOverGround} sog={v.SpeedOverGround}" +
            $" (rate-limited to one log/min)");
    }

    private static OwnContext? TryBuildOwnContext(NavigationData ownship, string ownVesselType)
    {
        var snap = Cpa.PrecomputeOwn(
            ownship.Latitude ?? double.NaN, ownship.Longitude ?? double.NaN,
            ownship.CourseOverGround, ownship.SpeedOverGround);
        if (snap is null) return null;
        // PrecomputeOwn already gates non-finite/null on each input;
        // by the time we get here lat/lon/cog/sog are guaranteed
        // finite, so the field unwraps below are safe.
        var ownType = ownVesselType == "sail"
            ? Colregs.VesselType.Sail
            : Colregs.VesselType.Power;
        return new OwnContext(
            snap.Value,
            ownship.Latitude!.Value, ownship.Longitude!.Value,
            ownship.CourseOverGround!.Value, ownship.SpeedOverGround!.Value,
            ownType);
    }

    /// <summary>Computes CPA + threat band for a single target.
    /// COLREGS classification is computed lazily (only when the
    /// threat band is Warning or Danger) - on a 200-vessel harbour
    /// at 3 Hz that drops ~600 wasted Colregs.Classify calls/sec,
    /// matching the eager-vs-lazy fix already in CpaAlarmRule.Check.
    /// COLREGS for a vessel currently outside the outer ring has no
    /// helm-actionable meaning anyway - the rules apply to closing
    /// encounters.</summary>
    private static VesselThreat ComputeVesselThreat(
        in OwnContext? ownCtx, AisVessel v, double vLat, double vLon,
        double effectiveCpaRadiusNm, double lookaheadMin)
    {
        if (ownCtx is null
            || v.CourseOverGround is null || v.SpeedOverGround is null)
        {
            return default;  // threat None, all fields null
        }
        var ctx = ownCtx.Value;
        var cpa = Cpa.Compute(
            ctx.Snap, vLat, vLon, v.CourseOverGround, v.SpeedOverGround);
        if (cpa is not { } c)
        {
            return default;
        }

        // CurrentDistanceNm is a free byproduct of the Cpa.Compute
        // projection (see Cpa.Result XML doc). Previously the chip
        // pipeline + CpaAlarmRule each ran their own haversine here.
        var threat = Cpa.ClassifyThreat(
            c.CpaNm, c.TcpaMin, c.CurrentDistanceNm,
            effectiveCpaRadiusNm, lookaheadMin, v.IsBuddy);

        string? colregsLabel = null;
        string? colregsRole = null;
        if (threat != Cpa.Threat.None)
        {
            // Type-aware classification: own from
            // IAppSettings.OwnVesselType (helm-picked), target from
            // design.aisShipType. When propulsion differs the role
            // is overridden by Rule 18 so the helm sees "sail stands
            // on / power gives way" on a mixed encounter regardless
            // of geometry.
            var tgtType = Colregs.FromAisShipType(v.ShipType);
            var r = Colregs.Classify(
                ctx.Lat, ctx.Lon, ctx.Cog, ctx.Sog,
                vLat, vLon,
                v.CourseOverGround.Value, v.SpeedOverGround.Value,
                ctx.OwnType, tgtType);
            if (r.Category != Colregs.Category.Indeterminate)
            {
                colregsLabel = Colregs.ShortLabel(r.Category);
                colregsRole = Colregs.RoleLabel(r.Role);
            }
        }
        return new VesselThreat(c.CpaNm, c.TcpaMin, threat, colregsLabel, colregsRole);
    }

    /// <summary>Acquires a pooled payload at the given index, growing
    /// the pool on the high-water-mark frame. Pool slots are reset
    /// in <see cref="FillPayload"/> field-by-field so V8 / .NET keep
    /// one hidden class for the type and stale values from a previous
    /// larger frame can't leak through.</summary>
    private AisVesselPayload AcquirePoolSlot(int index)
    {
        if (index < _payloadPool.Count) return _payloadPool[index];
        var p = new AisVesselPayload();
        _payloadPool.Add(p);
        return p;
    }

    /// <summary>Writes every JS-visible field of <paramref name="p"/>
    /// from the source vessel + per-tick threat result. Hand-written
    /// (not reflection / mapper) because the payload pool is in the
    /// hot path - one assignment per field is the cheapest shape that
    /// keeps the JIT and V8 happy.</summary>
    private static void FillPayload(
        AisVesselPayload p, AisVessel v, double vLat, double vLon,
        in VesselThreat threat, DateTime nowUtc, double aisCogVectorMinutes,
        AisTrailBuffer trailBuffer)
    {
        // Display name resolution: name -> mmsi -> null, with buddy
        // star prefix. Lifted out of JS so the chart label and any
        // future label-rendering surface share the same fallback chain.
        string? baseName = !string.IsNullOrEmpty(v.Name) ? v.Name
            : !string.IsNullOrEmpty(v.Mmsi) ? v.Mmsi
            : null;
        string? displayName = baseName is null
            ? null
            : (v.IsBuddy ? "★ " + baseName : baseName);

        p.Context = v.Context;
        p.Name = v.Name;
        p.Mmsi = v.Mmsi;
        p.Callsign = v.Callsign;
        p.DisplayName = displayName;
        p.Lat = vLat;
        p.Lon = vLon;
        p.HeadingRad = v.Heading;
        p.CogRad = v.CourseOverGround;
        p.SogMs = v.SpeedOverGround;
        p.ShipType = v.ShipType;
        // AIS-static dimensions (LOA + beam). Often absent - see
        // AisVessel.LengthOverallMeters comments. JS popup renders
        // the row only when at least one is non-null.
        p.LoaM = v.LengthOverallMeters;
        p.BeamM = v.BeamMeters;
        p.Buddy = v.IsBuddy;
        // Tell JS whether to render with an AIS or radar-ARPA icon.
        // Radar targets lose buddy/danger overlays too; JS looks at
        // this flag.
        p.Source = v.Source == TargetSource.Radar ? "radar" : "ais";
        // Pre-resolved visual fields so the JS layer is a dumb
        // renderer: one source of truth for the AIS palette + SART
        // classification in C#, tested in AisPaletteTests /
        // AisSartAlarmRuleTests.
        p.SartCategory = AisSart.CategoryFromAny(v.Mmsi, v.Context);
        p.GlyphCategory = AisPalette.ShipTypeCategory(v.ShipType);
        p.ShipColor = AisPalette.ShipTypeColor(v.ShipType);
        p.CpaNm = threat.CpaNm;
        p.TcpaMin = threat.TcpaMin;
        p.CpaThreat = ThreatToWireString(threat.Threat);
        p.ColregsLabel = threat.ColregsLabel;
        p.ColregsRole = threat.ColregsRole;
        // Seconds since we last heard from this target. JS uses it to
        // fade stale markers (>30 s) so the chart visually
        // distinguishes a live target from a ghost that hasn't
        // updated in minutes.
        p.AgeSec = (int)(nowUtc - v.LastSeen).TotalSeconds;

        // Precompute the COG-vector endpoint here so the JS hot path
        // doesn't run destPoint() per vessel per tick. GeoMath.VectorEnd
        // returns null when COG / SOG missing or SOG < 0.1 m/s; the JS
        // side treats null as "no vector drawn", matching what the
        // previous JS-side vectorEnd() did on the same inputs.
        var vec = GeoMath.VectorEnd(
            vLat, vLon, v.CourseOverGround, v.SpeedOverGround,
            aisCogVectorMinutes);
        if (vec is { } v0)
        {
            p.VectorEndLat = v0.Lat;
            p.VectorEndLon = v0.Lon;
        }
        else
        {
            p.VectorEndLat = null;
            p.VectorEndLon = null;
        }

        // CPA endpoint - the target's projected position at TCPA, drawn
        // by the JS layer as the far end of the crossing-situation
        // line. Only populated when the JS would actually use it
        // (threat is Warning or Danger). Otherwise null so JS can skip
        // the line entirely. The math is `destPoint` over a great
        // circle just like VectorEnd above, but with `sog * tcpaMin *
        // 60` as the projected distance instead of `sog * minutes * 60`.
        if (threat.Threat != Cpa.Threat.None
            && threat.TcpaMin is double tcpaMin
            && v.CourseOverGround is double cog
            && v.SpeedOverGround is double sog)
        {
            var cpaPt = GeoMath.DestPoint(vLat, vLon, cog, sog * tcpaMin * 60.0);
            p.CpaPointLat = cpaPt.Lat;
            p.CpaPointLon = cpaPt.Lon;
        }
        else
        {
            p.CpaPointLat = null;
            p.CpaPointLon = null;
        }

        // Trail update. Push the current fix into the per-vessel
        // sliding window; emit coords on the payload only when the
        // buffer signals a content change since the last emission.
        // - Same-position dedup happens inside the buffer.
        // - Age-trim runs there too, so a vessel whose trail expired
        //   between pushes flips dirty on the next push and JS
        //   receives the shortened (or null) trail.
        // - Stale vessels (not in `visible`) are forgotten in the
        //   loop's RetainOnly call after BuildSnapshot returns.
        bool trailChanged = trailBuffer.Push(v.Context ?? string.Empty, vLat, vLon, nowUtc);
        if (trailChanged && trailBuffer.ConsumeDirty(v.Context ?? string.Empty))
        {
            // GetCoords returns null when the trail is too short
            // (< 2 points) - JS reads null as "remove the existing
            // polyline if any". Distinct from "not present in the
            // payload" (which JS reads as "leave the existing trail
            // alone").
            p.Trail = trailBuffer.GetCoords(v.Context ?? string.Empty);
        }
        else
        {
            // The pooled payload may carry a stale Trail reference
            // from the previous snapshot for the same context. Clear
            // it so JS doesn't re-apply yesterday's trail to today's
            // marker; the JS reads `v.trail` as "treat as null when
            // absent, update only when present".
            p.Trail = null;
        }
    }

    /// <summary>Wire-string contract for the JS-side aisLayer.cpaThreat
    /// switch. Pinned in <c>AisPushServiceTests</c> so any rename here
    /// fails fast in C# rather than silently breaking the chart-overlay
    /// classifier on the JS side. The switch on the JS side reads
    /// <c>'danger' | 'warning' | _ -&gt; none</c>; keep these strings in
    /// sync with <c>wwwroot/js/aisLayer.js</c>.</summary>
    internal static string ThreatToWireString(Cpa.Threat t) => t switch
    {
        Cpa.Threat.Danger => "danger",
        Cpa.Threat.Warning => "warning",
        _ => "none",
    };
}
