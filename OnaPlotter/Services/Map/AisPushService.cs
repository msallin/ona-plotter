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
        _lastPushedVersion = storeVersion;
        _lastOwnLat = ownship.Latitude;
        _lastOwnLon = ownship.Longitude;
        _lastOwnCog = ownship.CourseOverGround;
        _lastOwnSog = ownship.SpeedOverGround;
        _lastHarborMode = harbor;
        _lastAnchorActive = ownship.AnchorActive;
        _lastAnchorMaxRadius = ownship.AnchorMaxRadius;
        _lastPushedAtUtc = nowUtc;

        try
        {
            var jsVessels = BuildSnapshot(ownship);
            await _aisJs.UpdateAisTargetsAsync(jsVessels);
        }
        catch (Microsoft.JSInterop.JSException ex)
        {
            // Hot path. Silent-but-logged stops a JS regression from
            // taking down the whole map UI. Console.WriteLine (not
            // Console.Error) so errorRelayBoot.js doesn't relay this
            // handled-and-recovered case as an unhandled error.
            Console.WriteLine($"[interop] PushAisTargets: {ex.Message}");
        }
    }

    private object[] BuildSnapshot(NavigationData ownship)
    {
        var vessels = _aisStore.GetVessels();
        // AisStore filters on position when building the snapshot, but
        // defend against someone clearing a position between snapshots
        // anyway -- force-unwrap is never worth the risk in a hot path.
        // Pre-compute COLREGS per vessel (requires own lat/lon/cog/sog) so
        // the JS popup can render the category + give-way role without
        // re-implementing the math on the JS side.
        double? ownLat = ownship.Latitude;
        double? ownLon = ownship.Longitude;
        double? ownCog = ownship.CourseOverGround;
        double? ownSog = ownship.SpeedOverGround;

        // Harbor-mode + position filter delegated to HarborAisFilter so
        // the moored-classification + cleanup-on-vanished-context
        // contract is unit-testable in isolation. The tracker's
        // nav.state + SOG-dwell rules decide what counts as moored;
        // this caller just trusts the answer.
        bool harbor = _settings.HarborMode;
        var now = _time.GetUtcNow().UtcDateTime;
        var visible = HarborAisFilter.Apply(vessels, _mooredTracker, harbor, now);

        // Effective CPA radius for THIS snapshot (anchor-narrowing
        // when applicable). The visible guard-zone rings AND the
        // chip classifier MUST agree -- using the underway threshold
        // here while the rings show the anchor-narrowed radius made
        // chips appear outside the rings ("how can this be?").
        // See OnaPlotter.Utilities.Cpa.EffectiveRadiusNm for the
        // single source of truth used by all three render paths
        // (rings, chips, alarm rule).
        double effectiveCpaRadiusNm = Cpa.EffectiveRadiusNm(
            _settings.CpaAlarmThreshold,
            ownship.AnchorActive,
            ownship.AnchorMaxRadius);

        // Hot path: 200+ vessels at ~3 Hz on a busy harbour push.
        // Pre-allocate the result array (visible.Count is known) and
        // walk via index instead of Select(...).ToArray() so the
        // closure capturing ownLat/ownLon/ownCog/ownSog/now/etc.
        // doesn't allocate per call -- the captured locals just
        // become method-frame locals, no heap.
        // Helper variables hoisted to keep the loop body short.
        var ownType = _settings.OwnVesselType == "sail"
            ? Colregs.VesselType.Sail
            : Colregs.VesselType.Power;
        // Pre-compute own-ship sin/cos of COG ONCE here rather than on
        // every vessel inside the loop -- the own-ship trig is
        // invariant across the per-target pass. Null when own-ship
        // inputs are missing/non-finite so the inner loop skips CPA.
        var ownSnap = Cpa.PrecomputeOwn(
            ownLat ?? double.NaN, ownLon ?? double.NaN, ownCog, ownSog);
        bool ownComplete = ownSnap is not null;
        var result = new object[visible.Count];
        for (int i = 0; i < visible.Count; i++)
        {
            var v = visible[i];
            // Pre-compute COLREGS + CPA in C# so (a) Utilities/Cpa.cs +
            // CpaTests is the single source of truth and (b) the
            // BuildVesselList / PushAisTargets paths can't drift.
            double? cpaNm = null;
            double? tcpaMin = null;
            string? colregsLabel = null;
            string? colregsRole = null;
            if (ownComplete
                && v.CourseOverGround is not null && v.SpeedOverGround is not null)
            {
                var cpa = Cpa.Compute(
                    ownSnap!.Value,
                    v.Latitude!.Value, v.Longitude!.Value,
                    v.CourseOverGround, v.SpeedOverGround);
                if (cpa is { } c)
                {
                    cpaNm = c.CpaNm;
                    tcpaMin = c.TcpaMin;
                }

                // Type-aware classification: own from
                // IAppSettings.OwnVesselType (helm-picked), target from
                // design.aisShipType. When propulsion differs the
                // role is overridden by Rule 18 so the helm sees
                // 'sail stands on / power gives way' on a mixed
                // encounter regardless of geometry.
                var tgtType = Colregs.FromAisShipType(v.ShipType);
                var r = Colregs.Classify(
                    ownLat!.Value, ownLon!.Value, ownCog!.Value, ownSog!.Value,
                    v.Latitude!.Value, v.Longitude!.Value,
                    v.CourseOverGround.Value, v.SpeedOverGround.Value,
                    ownType, tgtType);
                if (r.Category != Colregs.Category.Indeterminate)
                {
                    colregsLabel = Colregs.ShortLabel(r.Category);
                    colregsRole = Colregs.RoleLabel(r.Role);
                }
            }
            // Pre-resolved visual fields so the JS layer is a dumb
            // renderer: one source of truth for the AIS palette + SART
            // classification in C#, tested in AisPaletteTests /
            // AisSartAlarmRuleTests.
            string? sartCat = AisSart.CategoryFromAny(v.Mmsi, v.Context);
            string? glyphCategory = AisPalette.ShipTypeCategory(v.ShipType);

            // CPA threat band (none / warning / danger) is computed
            // here against the helm's guard-zone settings. JS used to
            // redo this thresholding inline; lifting it up means
            // CpaTests.ClassifyThreat is the single source of truth
            // and the alarm pipeline + map overlay can't disagree.
            // Uses the EFFECTIVE radius (computed once above) so chips
            // narrow to the anchor swing radius when anchored, matching
            // what the visible guard-zone rings show.
            var threat = Cpa.ClassifyThreat(
                cpaNm, tcpaMin,
                effectiveCpaRadiusNm,
                _settings.GuardZoneLookaheadMinutes,
                _settings.GuardZoneWarningFactor,
                v.IsBuddy);
            string cpaThreat = threat switch
            {
                Cpa.Threat.Danger => "danger",
                Cpa.Threat.Warning => "warning",
                _ => "none",
            };

            // Display name resolution: name -> mmsi -> null, with
            // buddy star prefix. Lifted out of JS so the chart label
            // and any future label-rendering surface share the same
            // fallback chain.
            string? baseName = !string.IsNullOrEmpty(v.Name) ? v.Name
                : !string.IsNullOrEmpty(v.Mmsi) ? v.Mmsi
                : null;
            string? displayName = baseName is null
                ? null
                : (v.IsBuddy ? "★ " + baseName : baseName);

            result[i] = new
            {
                context = v.Context, name = v.Name, mmsi = v.Mmsi, callsign = v.Callsign,
                displayName,                   // pre-resolved label or null
                lat = v.Latitude!.Value, lon = v.Longitude!.Value,
                headingRad = v.Heading, cogRad = v.CourseOverGround,
                sogMs = v.SpeedOverGround, shipType = v.ShipType,
                // AIS-static dimensions (LOA + beam). Often absent --
                // see AisVessel.LengthOverallMeters comments. JS popup
                // renders the row only when at least one is non-null.
                loaM = v.LengthOverallMeters,
                beamM = v.BeamMeters,
                buddy = v.IsBuddy,
                // Tell JS whether to render with an AIS or radar-ARPA
                // icon. Radar targets lose buddy/danger overlays too;
                // JS looks at this flag.
                source = v.Source == TargetSource.Radar ? "radar" : "ais",
                sartCategory = sartCat,        // "SART"/"MOB"/"EPIRB" or null
                glyphCategory,                 // "sail"/"fish"/"commercial"/"service"/null
                shipColor = AisPalette.ShipTypeColor(v.ShipType),
                cpaNm,                         // nautical miles or null
                tcpaMin,                       // minutes or null
                cpaThreat,                     // "none"/"warning"/"danger"
                colregsLabel,
                colregsRole,
                // Seconds since we last heard from this target. JS
                // uses it to fade stale markers (>30 s) so the chart
                // visually distinguishes a live target from a ghost
                // that hasn't updated in minutes.
                ageSec = (int)(now - v.LastSeen).TotalSeconds,
            };
        }
        return result;
    }
}
