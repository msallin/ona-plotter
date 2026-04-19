using OnaPlotter.Models;

namespace OnaPlotter.Services.Alarms;

/// <summary>
/// ANCHOR TIDE: warns when the boat is anchored and the next low-water
/// prediction plus the boat's draft says it's likely to touch bottom.
/// Needs both an active anchor (SignalK anchor-alarm plugin) AND a
/// tide plugin publishing <c>environment.tide.heightNow</c> +
/// <c>heightLow</c> + <c>timeLow</c>. Goes silent when either is
/// missing -- there's no reasonable fallback without tide data.
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

    /// <summary>Only look this far ahead. A LW that's 14 hours away
    /// isn't an actionable alarm -- sleep first, re-evaluate later.
    /// Six hours covers one full tidal half-cycle with some slack.</summary>
    private const double LookaheadHours = 6.0;

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        var d = ctx.Data;

        // Prerequisites: anchor down AND tide plugin feeding the bus.
        if (!d.AnchorActive) return null;
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

        double draft = ctx.Settings.BoatDraftMeters;
        double margin = ctx.Settings.AnchorTideSafetyMargin;
        double predictedDepth = depthNow - drop;
        double clearance = predictedDepth - draft;  // metres between keel and bottom at LW

        // No alarm if we still have the safety margin.
        if (clearance >= margin) return null;

        var hh = (int)Math.Floor(hoursToLw);
        var mm = (int)Math.Round((hoursToLw - hh) * 60);
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
}
