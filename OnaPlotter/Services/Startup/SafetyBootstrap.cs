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
    private readonly Action _activateSubscribers;
    private readonly ILogger _logger;

    public SafetyBootstrap(IServiceProvider services, ILogger<SafetyBootstrap> logger)
        : this(
            () => services.GetRequiredService<IMobService>().InitializeAsync(),
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

    internal SafetyBootstrap(Func<Task> initializeMob, Action activateSubscribers, ILogger logger)
    {
        _initializeMob = initializeMob;
        _activateSubscribers = activateSubscribers;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        BackgroundKick.Run(_initializeMob, _logger, "MOB recovery");
        _activateSubscribers();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
