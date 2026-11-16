namespace OnaPlotter.Utilities;

/// <summary>
/// Leading + trailing throttle for pages that re-render on
/// <c>SignalkClient.OnDataChanged</c>. SignalK deltas land per path, so a
/// well-instrumented boat raises 25-35 events per second; re-rendering a
/// page on each one starves input handling on a tablet. The first
/// <see cref="Request"/> in a quiet period fires immediately; requests
/// inside the interval coalesce into one trailing fire at the end of the
/// interval, so the last value always reaches the screen.
/// <para>
/// Not thread-safe beyond the pending-flag CAS: Blazor WASM delivers
/// requests on one thread, and the CAS keeps a re-entrant caller from
/// parking two trailing fires.
/// </para>
/// </summary>
public sealed class RenderThrottle : IDisposable
{
    private readonly long _intervalMs;
    private readonly Action _fire;
    private readonly Func<long> _nowMs;
    private readonly Func<int, Task> _delay;
    private long _lastFireMs;
    private bool _hasFired;
    private int _pending;
    private bool _disposed;

    /// <param name="interval">Minimum spacing between fires.</param>
    /// <param name="fire">Work to run, typically a render request.</param>
    /// <param name="nowMs">Monotonic clock in ms; tests inject a fake.</param>
    /// <param name="delay">Delay for the trailing fire; tests inject a gate.</param>
    public RenderThrottle(TimeSpan interval, Action fire,
        Func<long>? nowMs = null, Func<int, Task>? delay = null)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Interval must be positive.");
        _intervalMs = (long)interval.TotalMilliseconds;
        _fire = fire ?? throw new ArgumentNullException(nameof(fire));
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _delay = delay ?? (ms => Task.Delay(ms));
    }

    /// <summary>Ask for a fire. Runs now when the interval since the last
    /// fire has elapsed, otherwise schedules a single trailing fire.</summary>
    public void Request()
    {
        if (_disposed) return;
        long now = _nowMs();
        long elapsed = now - _lastFireMs;
        if (!_hasFired || elapsed >= _intervalMs)
        {
            Fire(now);
            return;
        }
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        _ = TrailingAsync((int)(_intervalMs - elapsed));
    }

    private async Task TrailingAsync(int delayMs)
    {
        await _delay(delayMs);
        _pending = 0;
        if (_disposed) return;
        Fire(_nowMs());
    }

    private void Fire(long now)
    {
        _lastFireMs = now;
        _hasFired = true;
        _fire();
    }

    /// <summary>Drop any parked trailing fire; later requests are ignored.</summary>
    public void Dispose() => _disposed = true;
}
