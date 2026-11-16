namespace OnaPlotter.Services.Map;

/// <summary>
/// Per-vessel sliding window of recent AIS positions. Owns trail growth,
/// age-based trim, and the bookkeeping for what the JS renderer already
/// holds, so <see cref="AisLayerController"/> can send only the points
/// appended and the number trimmed since the last emission instead of
/// the whole five-minute history on every change.
///
/// <para>Wire contract with <c>aisLayer.js</c>: a full sync sends every
/// point (<see cref="GetCoords"/>, then <see cref="MarkSynced"/>); after
/// that each <see cref="ConsumeDelta"/> says "drop <c>Trim</c> points
/// from the head, then append <c>Append</c>". JS draws the polyline once
/// it holds at least two points.</para>
///
/// <para>Concurrency: single-threaded by Blazor WASM contract. Every
/// caller runs on the renderer's sync context.</para>
/// </summary>
public sealed class AisTrailBuffer
{
    /// <summary>One sampled fix in a vessel's trail. <c>TicksUtc</c>
    /// is <see cref="DateTime.Ticks"/> in UTC so the trim path is a
    /// plain long-compare.</summary>
    public readonly record struct TrailPoint(double Lat, double Lon, long TicksUtc);

    /// <summary>Change since the last emission: drop <see cref="Trim"/>
    /// points from the head of the renderer's copy, then append
    /// <see cref="Append"/> (flat <c>[lat0, lon0, lat1, lon1, ...]</c>,
    /// null when nothing was appended).</summary>
    public readonly record struct TrailDelta(double[]? Append, int Trim)
    {
        /// <summary>True when the renderer's copy is already current.</summary>
        public bool IsEmpty => Append is null && Trim == 0;
    }

    /// <summary>Per-context trail state. <see cref="Points"/> is a queue
    /// so age-trim is O(1) at the head and append is O(1) at the tail;
    /// <see cref="Last"/> caches the tail for the position dedup.</summary>
    private sealed class TrailHistory
    {
        public readonly Queue<TrailPoint> Points = new();
        public TrailPoint Last { get; private set; }
        /// <summary>Points appended since the renderer last caught up.</summary>
        public int AppendedSinceEmit;
        /// <summary>Points trimmed from the head since the renderer last
        /// caught up. Includes appended points that expired before they
        /// were ever emitted; the renderer's trim is clamped to what it
        /// holds, so the arithmetic still lands on the same tail.</summary>
        public int TrimmedSinceEmit;
        /// <summary><see cref="EndSweep"/> generation of the last push.</summary>
        public int SweepGen;

        public void Append(TrailPoint pt)
        {
            Points.Enqueue(pt);
            Last = pt;
            AppendedSinceEmit++;
        }
    }

    private readonly Dictionary<string, TrailHistory> _trails = new(StringComparer.Ordinal);
    private int _sweepGen;
    private readonly TimeSpan _maxAge;

    /// <summary>Default constructor: 5-minute trail window.</summary>
    public AisTrailBuffer() : this(TimeSpan.FromMinutes(5)) { }

    /// <summary>Test seam: override the trail-age window.</summary>
    public AisTrailBuffer(TimeSpan maxAge)
    {
        _maxAge = maxAge;
    }

    /// <summary>Currently configured maximum trail age.</summary>
    public TimeSpan MaxAge => _maxAge;

    /// <summary>
    /// Records a new fix for <paramref name="context"/>. Returns
    /// <c>true</c> when the trail's visible geometry changed: a fresh
    /// point was appended, or points expired out of the window.
    ///
    /// <para>Position dedup: a fix whose lat / lon match the most recent
    /// point is not appended, so a moored vessel pinging the same
    /// coordinates doesn't grow its trail.</para>
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
        long cutoff = nowUtc.Ticks - _maxAge.Ticks;
        bool trimmed = false;
        while (trail.Points.Count > 0 && trail.Points.Peek().TicksUtc < cutoff)
        {
            trail.Points.Dequeue();
            trail.TrimmedSinceEmit++;
            trimmed = true;
        }
        // Liveness for the current sweep, whether or not the geometry
        // changed: a moored vessel is still live.
        trail.SweepGen = _sweepGen;
        return pushed || trimmed;
    }

    /// <summary>Test-only / inspection: trail length for
    /// <paramref name="context"/>, or 0 if the vessel isn't tracked.</summary>
    internal int CountFor(string context)
        => _trails.TryGetValue(context, out var trail) ? trail.Points.Count : 0;

    /// <summary>
    /// The whole trail as a flat <c>[lat0, lon0, lat1, lon1, ...]</c>
    /// array in insertion order, or <c>null</c> when the vessel has no
    /// points. Used for a full sync; pair with <see cref="MarkSynced"/>.
    /// </summary>
    public double[]? GetCoords(string context)
    {
        if (!_trails.TryGetValue(context, out var trail) || trail.Points.Count == 0)
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

    /// <summary>Record that the renderer now holds the whole trail
    /// (after a full sync), so the next delta starts from here.</summary>
    public void MarkSynced(string context)
    {
        if (!_trails.TryGetValue(context, out var trail)) return;
        trail.AppendedSinceEmit = 0;
        trail.TrimmedSinceEmit = 0;
    }

    /// <summary>
    /// What changed since the renderer last caught up, then resets the
    /// bookkeeping. Appended points are taken from the tail, bounded by
    /// what is still in the window.
    /// <para>Example: renderer holds [p1, p2]; since then p3 was
    /// appended and p1 expired. Delta = Trim 1, Append [p3].</para>
    /// </summary>
    public TrailDelta ConsumeDelta(string context)
    {
        if (!_trails.TryGetValue(context, out var trail)) return default;
        int appended = Math.Min(trail.AppendedSinceEmit, trail.Points.Count);
        double[]? append = null;
        if (appended > 0)
        {
            append = new double[appended * 2];
            int skip = trail.Points.Count - appended;
            int i = 0;
            foreach (var p in trail.Points)
            {
                if (skip-- > 0) continue;
                append[i++] = p.Lat;
                append[i++] = p.Lon;
            }
        }
        var delta = new TrailDelta(append, trail.TrimmedSinceEmit);
        trail.AppendedSinceEmit = 0;
        trail.TrimmedSinceEmit = 0;
        return delta;
    }

    /// <summary>Drop a vessel's trail entirely. Re-acquiring the same
    /// context later starts a fresh trail.</summary>
    public void Forget(string context) => _trails.Remove(context);

    /// <summary>
    /// End-of-tick stale sweep: drops every context that wasn't
    /// <see cref="Push"/>ed since the previous <see cref="EndSweep"/>.
    /// Allocates a small remove-list only when something is stale.
    ///
    /// <para>Pairing contract: calling EndSweep twice in a row without
    /// intervening Pushes drops every tracked context.</para>
    /// </summary>
    public void EndSweep()
    {
        List<string>? toRemove = null;
        foreach (var (ctx, trail) in _trails)
        {
            if (trail.SweepGen != _sweepGen)
            {
                toRemove ??= new List<string>();
                toRemove.Add(ctx);
            }
        }
        _sweepGen++;
        if (toRemove is null) return;
        foreach (var ctx in toRemove) _trails.Remove(ctx);
    }

    /// <summary>Drop every tracked trail. After this call the buffer
    /// behaves like a freshly-constructed instance.</summary>
    public void Clear()
    {
        _trails.Clear();
        _sweepGen = 0;
    }
}
