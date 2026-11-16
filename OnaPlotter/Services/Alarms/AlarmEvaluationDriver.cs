using Microsoft.Extensions.Logging;
using OnaPlotter.Models;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Services.Alarms;

/// <summary>
/// Drives <see cref="IAlarmManager.Evaluate(NavigationData, Func{IReadOnlyCollection{AisVessel}}, IAppSettings)"/>
/// from two sources:
/// <list type="bullet">
/// <item>data changes: every SignalK delta (<see cref="SignalkClient.OnDataChanged"/>)
/// and every server-notification mutation
/// (<see cref="ServerNotificationStore.OnPathChanged"/>), so alarms react
/// as soon as new data lands;</item>
/// <item>a <see cref="TickInterval"/> timer, so time-based rules keep
/// running when no data arrives. Without it DEADMAN would never fire while
/// the WebSocket is down, and server alarms dropped on disconnect
/// (<see cref="ServerNotificationStore.Reset"/> fires no event) would stay
/// on the banner until the next delta.</item>
/// </list>
/// The manager throttles to one evaluation per
/// <see cref="AlarmManager.EvaluationIntervalMs"/> and only then pulls
/// the AIS snapshot, so the 30 Hz delta stream costs a clock read per
/// delta rather than a snapshot rebuild.
/// <para>Singleton, resolved at startup so evaluation does not depend on
/// any component being mounted.</para>
/// </summary>
public sealed class AlarmEvaluationDriver : IDisposable
{
    /// <summary>Timer cadence. Matches the manager's evaluation throttle:
    /// faster would be throttled away, slower would delay DEADMAN and
    /// snooze-expiry by up to the gap.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(AlarmManager.EvaluationIntervalMs);

    private readonly IAlarmManager _alarms;
    private readonly ISignalkClient _signalk;
    private readonly ServerNotificationStore _notifications;
    private readonly IAppSettings _settings;
    private readonly ILogger<AlarmEvaluationDriver> _logger;
    private readonly Func<IReadOnlyCollection<AisVessel>> _vessels;
    private readonly ITimer _timer;

    public AlarmEvaluationDriver(
        IAlarmManager alarms,
        ISignalkClient signalk,
        AisStore ais,
        ServerNotificationStore notifications,
        IAppSettings settings,
        TimeProvider time,
        ILogger<AlarmEvaluationDriver> logger)
    {
        _alarms = alarms;
        _signalk = signalk;
        _notifications = notifications;
        _settings = settings;
        _logger = logger;
        // Cached delegate: the manager invokes it only when an evaluation
        // is actually due.
        _vessels = ais.GetVessels;
        _signalk.OnDataChanged += Evaluate;
        _notifications.OnPathChanged += HandleNotificationPathChanged;
        _timer = time.CreateTimer(_ => Evaluate(), null, TickInterval, TickInterval);
    }

    private void HandleNotificationPathChanged(string _) => Evaluate();

    /// <summary>One evaluation attempt (throttled by the manager). Entry
    /// point for event and timer callbacks, so a faulting rule is logged
    /// here instead of tearing down the SignalK receive loop or the timer;
    /// the next tick tries again.</summary>
    internal void Evaluate()
    {
        try
        {
            _alarms.Evaluate(_signalk.Data, _vessels, _settings);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Alarm evaluation failed");
        }
    }

    public void Dispose()
    {
        _signalk.OnDataChanged -= Evaluate;
        _notifications.OnPathChanged -= HandleNotificationPathChanged;
        _timer.Dispose();
    }
}
