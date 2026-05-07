using OnaPlotter.Models;

namespace OnaPlotter.Services.State;

/// <summary>
/// Persists the Stats page's last-loaded state across navigation
/// so a helm who jumps to /map to check a chart and comes back
/// doesn't have to re-pick the date range and re-tap Compute.
/// <para>
/// Lives as a Singleton in DI for the page session; cleared on a
/// hard reload but otherwise sticky. Holds the input fields (range
/// mode, picked timespan, custom range, resolution, daily-toggle)
/// AND the computed result (totals + daily rows + computeAttempted
/// flag) so the page renders identically to where the helm left it.
/// </para>
/// <para>
/// Helm-feedback 2026-04: "When navigating away from history or
/// stats, do not throw away but keep the last values."
/// </para>
/// </summary>
public sealed class StatsPageState
{
    // Defaults match the page's first-visit defaults so an empty
    // cache + the page binding read the same values.
    public string RangeMode { get; set; } = "preset";
    public string SelectedTimespan { get; set; } = "7d";
    public DateTime CustomFromLocal { get; set; } = DateTime.Now.Date.AddDays(-7);
    public DateTime CustomToLocal { get; set; } = DateTime.Now;
    public string Resolution { get; set; } = "5m";

    /// <summary>True when the helm tapped Compute at least once
    /// during this session. Drives the page's empty-state vs
    /// totals-grid render.</summary>
    public bool ComputeAttempted { get; set; }

    /// <summary>Whether the daily-breakdown table was expanded the
    /// last time the helm looked. Liveaboards/voyagers leave it
    /// open; daysailors close it; we round-trip the choice.</summary>
    public bool ShowDaily { get; set; }

    /// <summary>The last computed totals, or null if no compute has
    /// run yet. Cached as the actual record, not just the inputs -
    /// the helm sees the SAME numbers they saw, even if the
    /// underlying server data has shifted while they were on
    /// /map.</summary>
    public StatsTotals? Totals { get; set; }

    /// <summary>The daily rows that paired with <see cref="Totals"/>.</summary>
    public IReadOnlyList<DailyStats> Daily { get; set; } = [];
}
