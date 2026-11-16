using Microsoft.Extensions.Hosting;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Startup;

namespace OnaPlotter.Services.Auth;

/// <summary>
/// Probes <see cref="IAuthApi.GetLoginStatusAsync"/> for the topbar
/// not-logged-in chip: once at <see cref="StartAsync"/> (in the
/// background), then on a timer whose cadence each probe re-tunes. While
/// the chip is up the cadence is <see cref="WarningShownInterval"/> so a
/// sign-in via the SK admin tab clears it within seconds; otherwise
/// <see cref="WarningHiddenInterval"/>, just to catch a session expiry.
/// A failed probe (404 on a third-party server, transport error) yields a
/// null <see cref="Current"/> and the chip stays hidden. Components
/// request an extra probe with <see cref="RefreshAsync"/> (tab becoming
/// visible).
/// </summary>
public sealed class LoginStatusPoller : IHostedService, IDisposable
{
    public static readonly TimeSpan WarningShownInterval = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan WarningHiddenInterval = TimeSpan.FromMinutes(2);

    private readonly IAuthApi _api;
    private readonly TimeProvider _time;
    private readonly ILogger<LoginStatusPoller> _logger;
    private ITimer? _timer;
    private bool _disposed;
    // Coalesces overlapping probes: a network stall can leave one in
    // flight when the next tick fires, and queueing them only builds a
    // backlog of stale round-trips.
    private int _probeInFlight;

    public LoginStatusPoller(IAuthApi api, TimeProvider time, ILogger<LoginStatusPoller> logger)
    {
        _api = api;
        _time = time;
        _logger = logger;
    }

    /// <summary>Latest probe result; null before the first probe or when
    /// the endpoint is absent or failed.</summary>
    public LoginStatus? Current { get; private set; }

    /// <summary>Fires when whether the not-logged-in warning is showing
    /// changes. May be raised from a timer or network thread.</summary>
    public event Action? OnChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _timer is not null) return Task.CompletedTask;
        _timer = _time.CreateTimer(
            _ => _ = RefreshAsync(),
            state: null, WarningHiddenInterval, WarningHiddenInterval);
        BackgroundKick.Run(RefreshAsync, _logger, "login status probe");
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
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Probes now, unless a probe is already running. Never
    /// throws.</summary>
    public async Task RefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _probeInFlight, 1, 0) != 0) return;
        try
        {
            var status = await _api.GetLoginStatusAsync().ConfigureAwait(false);
            var wasShowing = Current is { ShouldShowLoginWarning: true };
            var nowShowing = status is { ShouldShowLoginWarning: true };
            Current = status;
            var next = nowShowing ? WarningShownInterval : WarningHiddenInterval;
            try { _timer?.Change(next, next); }
            catch (ObjectDisposedException) { }
            if (wasShowing != nowShowing) OnChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // Best-effort: the chip keeps its previous state until the
            // next probe.
            _logger.LogDebug(ex, "Login status probe failed");
        }
        finally
        {
            Interlocked.Exchange(ref _probeInFlight, 0);
        }
    }
}
