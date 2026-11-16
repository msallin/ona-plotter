namespace OnaPlotter.Services.Startup;

public static class StartupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the app's startup as hosted services. Requires
    /// <c>UpdateChecker</c>, <c>AuthSession</c>, <c>SignalkClient</c> and
    /// <c>ResourceStore</c> to be registered as singletons.
    /// <para>
    /// Hosted services start sequentially in registration order, before
    /// first render; <c>StartAsync</c> must not block on network or
    /// throw. Add new startup work here, after the services it depends
    /// on.
    /// </para>
    /// </summary>
    public static IServiceCollection AddOnaStartup(this IServiceCollection services)
    {
        // Order is load-bearing: settings first (auto-login and the
        // WebSocket read them), then the connection, then the services
        // that rely on the connection being under way.
        services.AddSingleton<IAutoLogin>(sp => sp.GetRequiredService<Auth.AuthSession>());
        services.AddSingleton<ISocketStarter>(sp => sp.GetRequiredService<SignalkClient>());
        services.AddSingleton<IResourceRefresher>(sp => sp.GetRequiredService<Resources.ResourceStore>());
        services.AddHostedService<SettingsBootstrap>();
        services.AddHostedService<ConnectionBootstrap>();
        services.AddHostedService<SafetyBootstrap>();
        // Forwarder: MainLayout injects the same singleton.
        services.AddHostedService(sp => sp.GetRequiredService<UpdateChecker>());
        return services;
    }
}
