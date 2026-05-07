using OnaPlotter.Models;

namespace OnaPlotter.Services.Alarms;

/// <summary>
/// ANCHOR TIDE: warns when the boat is anchored and the next low-water
/// prediction plus the boat's draft says it's likely to touch bottom.
/// Needs both an active anchor (SignalK anchor-alarm plugin) AND a
/// tide plugin publishing <c>environment.tide.heightNow</c> +
/// <c>heightLow</c> + <c>timeLow</c>. Goes silent when either is
/// missing - there's no reasonable fallback without tide data.
///
/// <para>Math: current under-keel depth at anchor time - (nowHeight -
/// lowHeight) = predicted depth at LW. If that's less than the user's
/// draft + safety margin, we alarm. "Depth" here comes from
/// <c>environment.depth.belowTransducer</c>, which for most boats is
/// close enough to under-keel for the alarm threshold; serious
/// absolute accuracy would need the boat's transducer offset, which
/// SignalK supports but most plugins don't populate.</para>
///
/// <para>Severity: Warn when LW clearance is below the margin but
/// still positive; Danger when the keel will touch.</para>
/// </summary>
public sealed class AnchorTideAlarmRule : IAlarmRule
{
    public string Title => "ANCHOR TIDE";

    // Sits between SHALLOW (100) and CPA (200): SHALLOW is an
    // immediate grounding, ANCHOR TIDE is a predicted one. Both
    // should surface above a collision warning because you have more
    // time to act on a distant ferry than on water leaving your keel.
    public int Priority => 150;

    // Auto-clears when tide swings back up, you re-anchor deeper,
    // or the plugin stops publishing. No latching.
    public bool AutoClear => true;

    /// <summary>Cross-plotter publish path. Sibling of
    /// <c>anchor.dragging</c> - both render under Title="ANCHOR"
    /// on receivers but the leaf differs so they clear independently.</summary>
    public string? GetPublishPath(AlarmInfo alarm) =>
        "notifications.navigation.anchor.tide";

    /// <summary>Only look this far ahead. A LW that's 14 hours away
    /// isn't an actionable alarm - sleep first, re-evaluate later.
    /// Six hours covers one full tidal half-cycle with some slack.</summary>
    private const double LookaheadHours = 6.0;

    // Once the helm dismisses ANCHOR TIDE, don't keep re-nagging every
    // 30s (the manager's default cooldown) - the warning is about a
    // predicted event hours away, so the helm's "yes, I know" should
    // stick for the duration of the current anchored session. We
    // re-arm when the anchor is lifted (AnchorActive transitions to
    // false), at which point the next anchoring gets a fresh warning.
    private bool _dismissedForThisAnchoring;

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        var d = ctx.Data;

        // Prerequisites: anchor down AND tide plugin feeding the bus.
        if (!d.AnchorActive)
        {
            // Anchor is up - reset the dismiss latch so the next
            // anchoring session starts with a clean rule.
            _dismissedForThisAnchoring = false;
            return null;
        }
        if (_dismissedForThisAnchoring) return null;
        if (d.Depth is not double depthNow) return null;
        if (d.TideHeightNow is not double heightNow) return null;
        if (d.TideHeightLow is not double heightLow) return null;
        if (d.TideTimeLow is not DateTime timeLow) return null;

        // Ignore LW that's too far out or in the past.
        var hoursToLw = (timeLow - ctx.Now).TotalHours;
        if (hoursToLw <= 0 || hoursToLw > LookaheadHours) return null;

        // Tidal change we expect between now and LW (positive = tide
        // falling). Multiplied by 1 m of depth per 1 m of tide drop.
        double drop = heightNow - heightLow;
        if (drop <= 0) return null;                // tide still rising

        // Draft comes from SignalK (design.draft.current / .maximum)
        // only - "don't re-enter what the bus already knows". The
        // tide-aware anchor alarm stays dormant when draft is absent
        // rather than run on a stale client-side default.
        if (ctx.Data.DraftFromSignalK is not double draft) return null;
        double margin = ctx.Settings.AnchorTideSafetyMargin;
        double predictedDepth = depthNow - drop;
        double clearance = predictedDepth - draft;  // metres between keel and bottom at LW

        // No alarm if we still have the safety margin.
        if (clearance >= margin) return null;

        var hh = (int)Math.Floor(hoursToLw);
        var mm = (int)Math.Round((hoursToLw - hh) * 60);
        // mm can land at 60 when hoursToLw rounds up at exactly the
        // hour boundary (e.g. hoursToLw = 0.999 -> hh=0, mm=Math.Round(59.94)=60).
        // Carry the rollover so the helm sees "1h00" instead of "60min".
        if (mm == 60) { hh++; mm = 0; }
        string when = hh == 0 ? $"{mm}min" : $"{hh}h{mm:D2}";

        double minutesToLw = hoursToLw * 60;

        if (clearance <= 0)
        {
            // Keel hits bottom at LW.
            double shortfall = -clearance;
            return new AlarmInfo(
                Title: Title,
                Message: $"Keel touches bottom at LW in {when} ({shortfall:F1}m short)",
                Severity: AlarmSeverity.Danger,
                TimeToEventMinutes: minutesToLw);
        }

        return new AlarmInfo(
            Title: Title,
            Message: $"Low clearance at LW in {when}: {clearance:F1}m under keel",
            Severity: AlarmSeverity.Warn,
            TimeToEventMinutes: minutesToLw);
    }

    public void OnDismissed(AlarmInfo dismissed, DateTime at)
    {
        // Latch the dismiss for this anchored session. Cleared in Check()
        // on the first tick after the anchor comes up.
        _dismissedForThisAnchoring = true;
    }
}
