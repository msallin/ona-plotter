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
}
