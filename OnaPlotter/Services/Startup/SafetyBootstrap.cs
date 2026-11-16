using Microsoft.Extensions.Hosting;
using OnaPlotter.Services.Alarms;
using OnaPlotter.Services.Mob;

namespace OnaPlotter.Services.Startup;

/// <summary>
/// Starts the services that must run on whichever page the helm loads
/// first, with no component injecting them:
/// <list type="bullet">
/// <item><c>IMobService.InitializeAsync</c> replays persisted MOB raises
/// from localStorage and reconciles with the server's active
/// notifications, so a man-overboard raised before a reload or on
/// another plotter reaches the banner even when the helm reloads on
/// Wind or Settings. Kicked in the background: an early 401 simply
/// no-ops and the reconcile on the first WebSocket connect picks up the
/// real list.</item>
/// <item><c>IAlarmManager.InitializeAsync</c> rehydrates snoozed targets
/// from the key-value store (localStorage), so a reload does not
/// forget that a target is silenced. Awaited before the alarm
/// subscribers are activated so no alarm is evaluated against an empty
/// snooze list; the read is local, like the settings load. A failure is
/// logged and the manager starts with no snoozes.</item>
/// <item><c>AlarmPublisher</c> (publishes local alarms to the server),
/// <c>AlarmEvaluationDriver</c> (data-driven and 1 Hz alarm evaluation),
/// <c>AutoAdvanceService</c> and <c>INavigationAverages</c> subscribe in
/// their constructors and nothing else injects them, so they are
/// resolved here. Resolving at <c>StartAsync</c> rather than by
/// constructor injection keeps them from being built before
/// <see cref="SettingsBootstrap"/> has loaded settings; hosted services
/// are all constructed before the first one starts.</item>
/// </list>
/// </summary>
public sealed class SafetyBootstrap : IHostedService
{
    private readonly Func<Task> _initializeMob;
    private readonly Func<Task> _initializeAlarms;
    private readonly Action _activateSubscribers;
    private readonly ILogger _logger;

    public SafetyBootstrap(IServiceProvider services, ILogger<SafetyBootstrap> logger)
        : this(
            () => services.GetRequiredService<IMobService>().InitializeAsync(),
            () => services.GetRequiredService<IAlarmManager>().InitializeAsync(),
            () =>
            {
                services.GetRequiredService<AlarmPublisher>();
                services.GetRequiredService<AlarmEvaluationDriver>();
                services.GetRequiredService<AutoAdvanceService>();
                services.GetRequiredService<INavigationAverages>();
            },
            logger)
    {
    }

    internal SafetyBootstrap(Func<Task> initializeMob, Func<Task> initializeAlarms,
        Action activateSubscribers, ILogger logger)
    {
        _initializeMob = initializeMob;
        _initializeAlarms = initializeAlarms;
        _activateSubscribers = activateSubscribers;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        BackgroundKick.Run(_initializeMob, _logger, "MOB recovery");
        try
        {
            await _initializeAlarms().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Snooze rehydration failed; starting with no snoozed targets");
        }
        _activateSubscribers();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
