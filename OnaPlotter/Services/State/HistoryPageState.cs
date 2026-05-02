namespace OnaPlotter.Services.State;

/// <summary>
/// Persists the History page's last-picked input state across
/// navigation so a helm who jumps to /map (or /resources) and
/// comes back doesn't have to re-pick the timespan + view mode.
/// <para>
/// Lives as a Singleton in DI for the page session. Stores INPUT
/// FIELDS only (range mode, picked timespan, custom range,
/// resolution, view mode). The loaded geometry (points, segments)
/// is intentionally NOT cached: the page auto-loads via the
/// existing LoadTrack on every mount, so the helm gets the same
/// view but with fresh data. That avoids the "year-old cache
/// shows the wrong trips" surprise after a long session.
/// </para>
/// <para>
/// Helm-feedback 2026-04: "When navigating away from history or
/// stats, do not throw away but keep the last values."
/// </para>
/// </summary>
public sealed class HistoryPageState
{
    public string RangeMode { get; set; } = "preset";
    // Defaults match the page's first-visit defaults so an empty cache
    // and the page-level field initialisers read the same values.
    // SelectedTimespan = "1d" + Resolution = "5m" pair to ~288 rows
    // for the default Today window, keeping the load to a single
    // round-trip on every link the helm is likely to use.
    public string SelectedTimespan { get; set; } = "1d";
    public DateTime CustomFromLocal { get; set; } = DateTime.Now.AddHours(-6);
    public DateTime CustomToLocal { get; set; } = DateTime.Now;
    public string Resolution { get; set; } = "5m";

    /// <summary>"map" or "table". Persists the helm's last view
    /// pick across navigation so a helm who was reading the trip
    /// list comes back to it, not to the playback map.</summary>
    public string ViewMode { get; set; } = "map";
}
