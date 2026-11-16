using Microsoft.Extensions.Hosting;

namespace OnaPlotter.Services.Startup;

/// <summary>
/// Loads persisted settings from localStorage. This is the one startup
/// step that is awaited before the next hosted service runs: auto-login
/// consults the Standalone-mode flag and stored credentials, and the
/// first WebSocket connect needs the server URL, so both must see the
/// loaded values rather than defaults. The read is local and fast, so
/// first paint is not noticeably delayed.
/// </summary>
public sealed class SettingsBootstrap : IHostedService
{
    private readonly IAppSettings _settings;

    public SettingsBootstrap(IAppSettings settings)
    {
        _settings = settings;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _settings.InitializeAsync();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
