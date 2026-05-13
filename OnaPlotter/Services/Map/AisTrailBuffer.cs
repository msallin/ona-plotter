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

    /// <summary>Per-context trail state. <see cref="Points"/> is a
    /// <see cref="Queue{T}"/> so age-trim is O(1) Dequeue from the
    /// head and append is O(1) Enqueue at the tail. <see cref="Last"/>
    /// caches the most recently appended point for the position-dedup
    /// check inside <see cref="Push"/> (Queue exposes head-peek but
    /// not tail-peek). Meaningful only when <c>Points.Count &gt; 0</c>;
    /// callers must always go through <see cref="Append"/> so the cache
    /// stays in sync with the queue.</summary>
    private sealed class TrailHistory
    {
        public readonly Queue<TrailPoint> Points = new();
        public TrailPoint Last { get; private set; }

        public void Append(TrailPoint pt)
        {
            Points.Enqueue(pt);
            Last = pt;
        }
    }

    private readonly Dictionary<string, TrailHistory> _trails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastEmittedVersions = new(StringComparer.Ordinal);
    /// <summary>Per-context generation stamp updated on every <see cref="Push"/>.
    /// <see cref="EndSweep"/> drops trails whose stamp doesn't match the
    /// current generation - lets the per-tick stale sweep avoid building
    /// a live-set HashSet from the snapshot.</summary>
    private readonly Dictionary<string, int> _pushedInSweep = new(StringComparer.Ordinal);
    private int _sweepGen;
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
        if (!_trails.TryGetValue(context, out var trail))
        {
            trail = new TrailHistory();
            _trails[context] = trail;
        }
        bool pushed = false;
        if (trail.Points.Count == 0
            || trail.Last.Lat != lat || trail.Last.Lon != lon)
        {
            trail.Append(new TrailPoint(lat, lon, nowUtc.Ticks));
            pushed = true;
        }
        // Trim front: drop oldest points whose age has crossed the
        // window. Queue.Dequeue is O(1).
        long cutoff = nowUtc.Ticks - _maxAge.Ticks;
        bool trimmed = false;
        while (trail.Points.Count > 0 && trail.Points.Peek().TicksUtc < cutoff)
        {
            trail.Points.Dequeue();
            trimmed = true;
        }
        // Stamp liveness for the current sweep regardless of whether
        // the geometry changed - a moored vessel pinging the same coords
        // is still "live" and must not be EndSwept away.
        _pushedInSweep[context] = _sweepGen;
        if (pushed || trimmed)
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
        => _trails.TryGetValue(context, out var trail) ? trail.Points.Count : 0;

    /// <summary>
    /// Snapshot the vessel's trail as a flat alternating
    /// <c>[lat0, lon0, lat1, lon1, ...]</c> array. Returns <c>null</c>
    /// when fewer than two points exist - mirrors the JS-side
    /// <c>hist.length &lt; 2</c> guard that suppresses degenerate
    /// single-vertex polylines.
    ///
    /// <para>Single allocation per call (the outer <c>double[]</c>);
    /// JS unpacks pairs in a single forward loop. Intended to be
    /// invoked only when <see cref="ConsumeDirty"/> returns true,
    /// i.e. once per (vessel, content-change) tuple.</para>
    /// </summary>
    public double[]? GetCoords(string context)
    {
        if (!_trails.TryGetValue(context, out var trail) || trail.Points.Count < 2)
            return null;
        var coords = new double[trail.Points.Count * 2];
        int i = 0;
        foreach (var p in trail.Points)
        {
            coords[i++] = p.Lat;
            coords[i++] = p.Lon;
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
        _pushedInSweep.Remove(context);
    }

    /// <summary>
    /// End-of-tick stale sweep: drops every context that wasn't
    /// <see cref="Push"/>ed since the previous <see cref="EndSweep"/>.
    /// One call per AisPushService snapshot. Allocates only a small
    /// remove-list, and only when something is actually stale - in the
    /// steady-state case where every trail was refreshed this tick the
    /// method is a single dictionary scan with zero allocations.
    ///
    /// <para>Pairing contract: each call retires contexts that had no
    /// Push since the previous EndSweep. Calling EndSweep twice in a
    /// row without intervening Pushes will drop every tracked context -
    /// the second sweep finds none stamped with the new generation.</para>
    /// </summary>
    public void EndSweep()
    {
        List<string>? toRemove = null;
        foreach (var (ctx, gen) in _pushedInSweep)
        {
            if (gen != _sweepGen)
            {
                toRemove ??= new List<string>();
                toRemove.Add(ctx);
            }
        }
        _sweepGen++;
        if (toRemove is null) return;
        foreach (var ctx in toRemove) Forget(ctx);
    }

    /// <summary>Drop every tracked trail. Used on dispose / page
    /// teardown / a manual reset. After this call the buffer behaves
    /// like a freshly-constructed instance.</summary>
    public void Clear()
    {
        _trails.Clear();
        _versions.Clear();
        _lastEmittedVersions.Clear();
        _pushedInSweep.Clear();
        _sweepGen = 0;
    }
}
