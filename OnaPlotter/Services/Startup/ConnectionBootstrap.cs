using Microsoft.Extensions.Hosting;

namespace OnaPlotter.Services.Startup;

/// <summary>
/// Brings up the SignalK connection. The steps are order-coupled, so
/// they live in one class:
/// <list type="number">
/// <item>The resource refresher (<c>ResourceStore</c>) and <see cref="RegionStore"/> are
/// injected, which constructs them: their constructors subscribe to
/// <c>SignalkClient.OnResourceDelta</c> / <c>OnConnectionChanged</c> and
/// to the store's region events. Both must be wired before any WebSocket
/// frame flows, and <see cref="RegionStore"/> keeps
/// <c>HazardousRegionAlarmRule</c> fed whether or not the map page is
/// mounted.</item>
/// <item>Auto-login is kicked off in the background. It must be started
/// before the socket so the first connect can carry the token, but it
/// must never block: a slow or failed login on an offline boot would
/// otherwise hold the loading screen. <c>SignalkClient</c> reconnects on
/// <c>ITokenStore.OnTokenChanged</c> when the login lands.</item>
/// <item>The WebSocket receive loop starts.</item>
/// <item>The initial REST reconcile of resources starts in the
/// background; pages poll <c>IsLoaded</c> or subscribe to
/// <c>OnReloaded</c>.</item>
/// </list>
/// Must be registered after <see cref="SettingsBootstrap"/>, because
/// auto-login reads the loaded settings. The injected dependencies
/// (auth, API clients, stores) are constructed before settings load, so
/// their constructors must not read settings.
/// </summary>
public sealed class ConnectionBootstrap : IHostedService
{
    private readonly IAutoLogin _auth;
    private readonly ISocketStarter _socket;
    private readonly IResourceRefresher _resources;
    private readonly ILogger<ConnectionBootstrap> _logger;

    public ConnectionBootstrap(
        IResourceRefresher resources,
        RegionStore regions,
        IAutoLogin auth,
        ISocketStarter socket,
        ILogger<ConnectionBootstrap> logger)
    {
        // regions is injected only for its constructor-time subscriptions.
        _ = regions;
        _resources = resources;
        _auth = auth;
        _socket = socket;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        BackgroundKick.Run(() => _auth.TryAutoLoginAsync(), _logger, "auto-login");
        BackgroundKick.Run(() => _socket.StartAsync(), _logger, "signalk websocket start");
        BackgroundKick.Run(() => _resources.RefreshAllAsync(cause: "startup"), _logger, "initial resource refresh");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
