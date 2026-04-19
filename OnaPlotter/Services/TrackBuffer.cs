using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// Thread-safe ring buffer that stores recent vessel track points for map trails
/// and wind rose history. Old points are silently overwritten when capacity is reached.
/// </summary>
public sealed class TrackBuffer
{
    private readonly Lock _lock = new();
    private readonly TrackPoint[] _buffer;
    private int _head;
    private int _count;

    public event Action? OnTrackUpdated;

    public TrackBuffer(int capacity = 1000)
    {
        _buffer = new TrackPoint[capacity];
    }

    public void Add(TrackPoint point)
    {
        lock (_lock)
        {
            _buffer[_head] = point;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length)
                _count++;
        }

        OnTrackUpdated?.Invoke();
    }

    /// <summary>
    /// Returns a snapshot of all track points in chronological order (oldest first).
    /// </summary>
    public TrackPoint[] GetSnapshot()
    {
        lock (_lock)
        {
            var result = new TrackPoint[_count];
            if (_count < _buffer.Length)
            {
                Array.Copy(_buffer, 0, result, 0, _count);
            }
            else
            {
                int start = _head; // oldest element
                int tailLen = _buffer.Length - start;
                Array.Copy(_buffer, start, result, 0, tailLen);
                Array.Copy(_buffer, 0, result, tailLen, start);
            }
            return result;
        }
    }

    public int Count
    {
        get { lock (_lock) { return _count; } }
    }

    /// <summary>
    /// Returns the signed change in depth over the given lookback window,
    /// in metres per minute. Positive = depth increasing (deeper);
    /// negative = decreasing (shallower). Returns null when there's
    /// insufficient data (no depth samples in the window, or the buffer
    /// doesn't yet span the window).
    /// </summary>
    /// <remarks>
    /// Uses the first and last depth samples in the window for a
    /// coarse linear rate. Finer-grained fit (least squares) would
    /// be noise-smoothed but this is good enough for a helm indicator
    /// that answers "getting shallower fast?" in a tidal channel.
    /// </remarks>
    public double? GetDepthTrendMetersPerMinute(TimeSpan window)
    {
        if (window.TotalSeconds <= 0) return null;
        lock (_lock)
        {
            if (_count < 2) return null;
            // Walk newest-to-oldest; collect the newest sample with a
            // depth, then the oldest-but-still-in-window sample.
            var now = DateTime.UtcNow;
            var cutoff = now - window;
            TrackPoint? newest = null;
            TrackPoint? oldestInWindow = null;
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - 1 - i + _buffer.Length) % _buffer.Length;
                var p = _buffer[idx];
                if (p.Depth is null) continue;
                if (newest is null) newest = p;
                if (p.Timestamp < cutoff) break;
                oldestInWindow = p;
            }
            if (newest is null || oldestInWindow is null) return null;
            if (newest.Timestamp == oldestInWindow.Timestamp) return null;

            double dMetres = newest.Depth!.Value - oldestInWindow.Depth!.Value;
            double dMinutes = (newest.Timestamp - oldestInWindow.Timestamp).TotalMinutes;
            if (dMinutes < 0.05) return null; // <3 s spread is noise
            return dMetres / dMinutes;
        }
    }
}
