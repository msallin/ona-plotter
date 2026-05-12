namespace OnaPlotter.Services.Map;

/// <summary>
/// Per-vessel sliding-window of recent AIS positions, the C# home of
/// what used to be a JS-side <c>aisTrailHistory</c> dictionary in
/// <c>aisLayer.js</c>. Owns trail growth, age-based trim, and
/// change-tracking so <see cref="AisPushService"/> can emit fresh
/// coords on the per-vessel <see cref="AisVesselPayload.Trail"/> field
/// only when the trail actually changed since the last push.
///
/// <para>Default trail window matches the JS-side <c>AIS_TRAIL_SECONDS</c>
/// of 5 minutes - configurable via the constructor for tests.</para>
///
/// <para>Concurrency: single-threaded by Blazor WASM contract. Every
/// caller runs on the renderer's sync context.</para>
/// </summary>
public sealed class AisTrailBuffer
{
    /// <summary>One sampled fix in a vessel's trail. <c>TicksUtc</c>
    /// is <see cref="DateTime.Ticks"/> in UTC so the trim path is a
    /// plain long-compare (no timezone math per-point per-vessel).</summary>
    public readonly record struct TrailPoint(double Lat, double Lon, long TicksUtc);

    private readonly Dictionary<string, List<TrailPoint>> _trails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastEmittedVersions = new(StringComparer.Ordinal);
    private readonly TimeSpan _maxAge;

    /// <summary>Default constructor: 5-minute window mirroring the JS
    /// <c>AIS_TRAIL_SECONDS = 300</c> constant.</summary>
    public AisTrailBuffer() : this(TimeSpan.FromMinutes(5)) { }

    /// <summary>Test seam: override the trail-age window. Production
    /// uses the parameterless ctor.</summary>
    public AisTrailBuffer(TimeSpan maxAge)
    {
        _maxAge = maxAge;
    }

    /// <summary>Currently configured maximum trail age. Exposed for
    /// the tests + the JS-equivalent constant documentation.</summary>
    public TimeSpan MaxAge => _maxAge;

    /// <summary>
    /// Records a new fix for <paramref name="context"/>. Returns
    /// <c>true</c> when the trail's *visible geometry* changed - either
    /// a fresh point was appended, or one-or-more points expired out
    /// of the trail window. False means the buffer is byte-identical
    /// to what was here at the start of the call, and the caller can
    /// skip the per-vessel <see cref="ConsumeDirty"/> probe entirely
    /// (or use the return value directly).
    ///
    /// <para>Position dedup: a fix whose lat / lon match the most-
    /// recent point is NOT appended. Matches the original JS dedup
    /// at <c>aisLayer.updateAisTrail</c> - a moored vessel pinging
    /// the same coords every 3 s wouldn't extend its trail.</para>
    /// </summary>
    public bool Push(string context, double lat, double lon, DateTime nowUtc)
    {
        if (!_trails.TryGetValue(context, out var hist))
        {
            hist = new List<TrailPoint>();
            _trails[context] = hist;
        }
        bool pushed = false;
        if (hist.Count == 0
            || hist[^1].Lat != lat || hist[^1].Lon != lon)
        {
            hist.Add(new TrailPoint(lat, lon, nowUtc.Ticks));
            pushed = true;
        }
        // Trim front: drop the oldest points whose age has crossed
        // the window. List.RemoveAt(0) is O(n) - acceptable here
        // because the trail length is bounded by the trail window and
        // the SK sample cadence (a 300 s window at 1 Hz tops out
        // around 300 points). For larger windows or higher cadence
        // a head-index ring buffer would be the next step.
        long cutoff = nowUtc.Ticks - _maxAge.Ticks;
        int dropped = 0;
        while (hist.Count > 0 && hist[0].TicksUtc < cutoff)
        {
            hist.RemoveAt(0);
            dropped++;
        }
        if (pushed || dropped > 0)
        {
            _versions.TryGetValue(context, out int v);
            _versions[context] = v + 1;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Test-only / inspection: returns the trail length for
    /// <paramref name="context"/>, or 0 if the vessel isn't tracked.
    /// </summary>
    internal int CountFor(string context)
        => _trails.TryGetValue(context, out var hist) ? hist.Count : 0;

    /// <summary>
    /// Snapshot the vessel's trail as the wire-format <c>[lat, lon]</c>
    /// coord array (one entry per stored point). Returns <c>null</c>
    /// when fewer than two points exist - mirrors the JS-side
    /// <c>hist.length &lt; 2</c> guard that suppresses degenerate
    /// single-vertex polylines.
    ///
    /// <para>Allocates a fresh outer array + nested 2-tuples on each
    /// call. Intended to be invoked only when <see cref="ConsumeDirty"/>
    /// returns true, i.e. once per (vessel, content-change) tuple. If
    /// caller ever wants this on every tick, a flat <c>double[]</c>
    /// (alternating lat / lon) over interop would halve the wire
    /// bytes - kept as nested arrays for v1 since JS reads
    /// <c>[lat, lon]</c> pairs directly into <c>L.polyline</c>.</para>
    /// </summary>
    public double[][]? GetCoords(string context)
    {
        if (!_trails.TryGetValue(context, out var hist) || hist.Count < 2)
            return null;
        var coords = new double[hist.Count][];
        for (int i = 0; i < hist.Count; i++)
        {
            coords[i] = new[] { hist[i].Lat, hist[i].Lon };
        }
        return coords;
    }

    /// <summary>
    /// Returns true once-per-content-change. The first call after a
    /// successful <see cref="Push"/> returns true and snapshots the
    /// version; subsequent calls return false until the next change.
    /// Lets <see cref="AisPushService.FillPayload"/> decide "should I
    /// emit fresh coords on the wire this tick?" with one cheap
    /// dictionary probe per vessel.
    /// </summary>
    public bool ConsumeDirty(string context)
    {
        if (!_versions.TryGetValue(context, out int v)) return false;
        _lastEmittedVersions.TryGetValue(context, out int last);
        if (v == last) return false;
        _lastEmittedVersions[context] = v;
        return true;
    }

    /// <summary>
    /// Drop a vessel's trail entirely. Called when the vessel falls
    /// out of <c>AisStore.GetVessels()</c> (aged-out / harbor-mode-
    /// filtered / context vanished). Re-acquiring the same context
    /// later starts a fresh trail - matches the JS behaviour where
    /// the stale-sweep deleted <c>aisTrailHistory[ctx]</c>.
    /// </summary>
    public void Forget(string context)
    {
        _trails.Remove(context);
        _versions.Remove(context);
        _lastEmittedVersions.Remove(context);
    }

    /// <summary>
    /// Drop every context not in <paramref name="liveContexts"/>.
    /// One-shot stale-sweep for the AisPushService loop: after the
    /// per-vessel pass, the caller knows which contexts are still
    /// visible; everything else is gone. Allocates a HashSet copy
    /// of <paramref name="liveContexts"/> only when the buffer has
    /// more than a few stale candidates - cheap when the live set
    /// dominates the cached set (the steady-state case).
    /// </summary>
    public void RetainOnly(IEnumerable<string> liveContexts)
    {
        var live = liveContexts as HashSet<string>
                   ?? new HashSet<string>(liveContexts, StringComparer.Ordinal);
        if (_trails.Count == 0) return;
        // ToArray() so we can mutate the dict mid-iteration.
        foreach (var ctx in _trails.Keys.ToArray())
        {
            if (!live.Contains(ctx)) Forget(ctx);
        }
    }

    /// <summary>Drop every tracked trail. Used on dispose / page
    /// teardown / a manual reset. After this call the buffer behaves
    /// like a freshly-constructed instance.</summary>
    public void Clear()
    {
        _trails.Clear();
        _versions.Clear();
        _lastEmittedVersions.Clear();
    }
}
