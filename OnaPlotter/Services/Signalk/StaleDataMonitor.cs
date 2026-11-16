using Microsoft.Extensions.Hosting;

namespace OnaPlotter.Services.Signalk;

/// <summary>
/// Watches <see cref="ISignalkClient.IsDataStale"/> and raises
/// <see cref="OnChanged"/> when the stale state flips, so the connection
/// chip re-renders without any component owning a timer. The check runs
/// every <see cref="CheckIntervalSeconds"/> seconds from
/// <see cref="StartAsync"/>; a data frame arriving while stale clears the
/// state immediately instead of waiting for the next tick.
/// </summary>
public sealed class StaleDataMonitor : IHostedService, IDisposable
{
    public const int CheckIntervalSeconds = 2;

    private readonly ISignalkClient _client;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private volatile bool _wasStale;
    private bool _disposed;

    public StaleDataMonitor(ISignalkClient client, TimeProvider time)
    {
        _client = client;
        _time = time;
    }

    /// <summary>Fires when the stale state flips in either direction.
    /// May be raised from a timer or WebSocket thread.</summary>
    public event Action? OnChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _timer is not null) return Task.CompletedTask;
        _client.OnDataChanged += HandleDataChanged;
        var interval = TimeSpan.FromSeconds(CheckIntervalSeconds);
        _timer = _time.CreateTimer(_ => Check(), state: null, dueTime: interval, period: interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _disposed = true;
        _client.OnDataChanged -= HandleDataChanged;
        _timer?.Dispose();
        _timer = null;
    }

    private void Check()
    {
        var isStale = _client.IsDataStale;
        if (isStale == _wasStale) return;
        _wasStale = isStale;
        OnChanged?.Invoke();
    }

    private void HandleDataChanged()
    {
        if (!_wasStale) return;
        _wasStale = false;
        OnChanged?.Invoke();
    }
}
