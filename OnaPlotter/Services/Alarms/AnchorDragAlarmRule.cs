using OnaPlotter.Models;

namespace OnaPlotter.Services.Alarms;

/// <summary>ANCHOR DRAG: fires when the boat has drifted outside its set
/// anchor radius. The signalk-anchoralarm-plugin computes
/// <c>navigation.anchor.currentRadius</c> from live position against the
/// drop point; this rule raises the client-side alarm when current &gt;
/// max. Previously the only indication was a red pill on the anchor HUD
/// card -- silent to a helmsman asleep below decks. v1 treats anchor
/// drag as a Danger-tier audible alarm alongside the existing tide and
/// shallow rules.
///
/// <para>Hysteresis: to avoid chattering when a gust momentarily nudges
/// the boat onto the ring, the current radius must exceed max by at
/// least <see cref="HysteresisMeters"/> to trip. The alarm clears
/// (auto-clear) once the boat is safely back inside max minus the
/// same band. Both thresholds are tunable constants, not settings --
/// the max radius is already a user input on the plugin side; this
/// just adds a dead-band around it.</para>
///
/// <para>When the server anchor isn't active (no drop point), the rule
/// is dormant. When depths or position drop out the server's
/// currentRadius freezes; this rule only fires on the LAST value it
/// saw, so a dying sensor stops triggering new alarms. Catching that
/// dying-sensor case is a separate concern, handled by the staleness
/// indicator on the HUD.</para>
/// </summary>
public sealed class AnchorDragAlarmRule : IAlarmRule
{
    public string Title => "ANCHOR DRAG";

    /// <summary>Highest priority after grounding / shallow / tide -- a
    /// dragging anchor at night is the same severity class as running
    /// aground.</summary>
    public int Priority => 140;    // between SHALLOW (100) / ANCHOR TIDE (150) -- both danger-class

    public bool AutoClear => true;

    /// <summary>Dead-band in metres. The alarm trips when currentRadius
    /// &gt; maxRadius + this, and clears when currentRadius &lt;
    /// maxRadius - this. 2 m is wider than typical GPS jitter on a
    /// swinging bow, narrow enough to catch a real drag within one
    /// boat-length on most cruising boats.</summary>
    public const double HysteresisMeters = 2.0;

    // Whether we're currently inside the alarmed band. Persistent so
    // the clear hysteresis works across ticks.
    private bool _alarmed;

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        var data = ctx.Data;
        if (!data.AnchorActive) { _alarmed = false; return null; }
        if (data.AnchorMaxRadius is not double max
            || data.AnchorCurrentRadius is not double cur)
        {
            // Server anchor active but radius fields haven't arrived yet
            // (startup race). Stay quiet; on next tick the delta lands.
            return null;
        }
        if (!double.IsFinite(max) || !double.IsFinite(cur)) return null;

        // Don't trust a frozen currentRadius: if the anchor-alarm plugin
        // stopped publishing (server crashed, network dropped, GPS died)
        // the last seen value sits on the model forever. Raising a drag
        // alarm off stale data would be a false positive; staying silent
        // off stale data would mask a real drag that coincided with the
        // dropout. The lesser evil is to not alarm on stale data and
        // rely on the connection-status indicator to flag the drop.
        // The HUD separately surfaces the staleness so the helm knows.
        if (data.FreshnessOf(data.AnchorRadiusUpdatedUtc) == FieldFreshness.Dead)
        {
            // Don't clear _alarmed; if we were alarmed before the dropout,
            // the last banner stays up (AutoClear=true on the manager
            // will decide to evict once we return null a few times).
            return null;
        }

        double upper = max + HysteresisMeters;
        double lower = max - HysteresisMeters;

        if (_alarmed)
        {
            // Clear only once safely back inside the band.
            if (cur < lower) { _alarmed = false; return null; }
            return BuildInfo(cur, max);
        }

        // Not currently alarmed. Trip when we punch through the upper band.
        if (cur > upper)
        {
            _alarmed = true;
            return BuildInfo(cur, max);
        }
        return null;
    }

    private AlarmInfo BuildInfo(double cur, double max) => new(
        Title: Title,
        Message: $"Dragging: {cur:F0}m / {max:F0}m radius",
        Severity: AlarmSeverity.Danger,
        TimeToEventMinutes: 0);
}
