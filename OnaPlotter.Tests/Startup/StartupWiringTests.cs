using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;
using OnaPlotter.Services.Mob;
using OnaPlotter.Services.Startup;

namespace OnaPlotter.Tests.Startup;

/// <summary>
/// Drives the production constructors through DI, the way the WASM host
/// does: every hosted service is constructed first, then StartAsync runs
/// sequentially in registration order.
/// </summary>
public class StartupWiringTests
{
    private sealed class Events
    {
        public List<string> Log { get; } = new();
        public void Add(string e) => Log.Add(e);
    }

    private sealed class FakeConnection : IAutoLogin, ISocketStarter, IResourceRefresher
    {
        private readonly Events _events;
        public FakeConnection(Events events) { _events = events; events.Add("ctor:connection-deps"); }
        public Task<bool> TryAutoLoginAsync(CancellationToken ct = default) { _events.Add("login"); return new TaskCompletionSource<bool>().Task; }
        public Task StartAsync(CancellationToken ct = default) { _events.Add("socket"); return Task.CompletedTask; }
        public Task RefreshAllAsync(string cause = "manual", CancellationToken ct = default) { _events.Add("refresh:" + cause); return new TaskCompletionSource().Task; }
    }

    private class MobProxy : DispatchProxy
    {
        private Events _events = null!;
        public IMobService Init(Events e) { _events = e; return (IMobService)(object)this; }
        protected override object? Invoke(MethodInfo? m, object?[]? a)
        {
            if (m?.Name == nameof(IMobService.InitializeAsync)) { _events.Add("mob"); return new TaskCompletionSource().Task; }
            throw new NotSupportedException(m?.Name);
        }
    }

    private class AlarmsProxy : DispatchProxy
    {
        private Events _events = null!;
        public IAlarmManager Init(Events e) { _events = e; return (IAlarmManager)(object)this; }
        protected override object? Invoke(MethodInfo? m, object?[]? a)
        {
            if (m?.Name == nameof(IAlarmManager.InitializeAsync)) { _events.Add("alarms"); return Task.CompletedTask; }
            throw new NotSupportedException(m?.Name);
        }
    }

    private static T Uninit<T>(Events e, string name) where T : class
    {
        e.Add("ctor:" + name);
        return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    }

    private static ServiceProvider Build(Events e)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new HttpClient());
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton<UpdateChecker>();
        services.AddOnaStartup();
        // Test doubles replace the production forwarders (last wins).
        services.AddSingleton(sp => new FakeConnection(e));
        services.AddSingleton<IAutoLogin>(sp => sp.GetRequiredService<FakeConnection>());
        services.AddSingleton<ISocketStarter>(sp => sp.GetRequiredService<FakeConnection>());
        services.AddSingleton<IResourceRefresher>(sp => sp.GetRequiredService<FakeConnection>());
        services.AddSingleton(sp => { e.Add("ctor:regions"); return new RegionStore(); });
        services.AddSingleton(StartupBootstrapTests.SettingsProxy.Create(() => { e.Add("settings"); return Task.CompletedTask; }));
        services.AddSingleton(MobProxyFor(e));
        services.AddSingleton(((AlarmsProxy)(object)DispatchProxy.Create<IAlarmManager, AlarmsProxy>()).Init(e));
        services.AddSingleton<ISignalkClient>(new FakeSignalkClient());
        services.AddSingleton<OnaPlotter.Services.Api.IAuthApi>(new LoginStatusPollerTests.FakeAuthApi());
        services.AddSingleton(sp => Uninit<AlarmPublisher>(e, "publisher"));
        services.AddSingleton(sp => Uninit<AlarmEvaluationDriver>(e, "driver"));
        services.AddSingleton(sp => Uninit<AutoAdvanceService>(e, "autoadvance"));
        services.AddSingleton<INavigationAverages>(sp => { e.Add("ctor:averages"); return new NavigationAverages(TimeProvider.System); });
        return services.BuildServiceProvider();
    }

    private static IMobService MobProxyFor(Events e) =>
        DispatchProxy.Create<IMobService, MobProxy>() is var p && p is not null
            ? ((MobProxy)(object)p).Init(e)
            : throw new InvalidOperationException();

    [Test]
    public async Task Production_Constructors_Construct_Deps_Then_Start_In_Order()
    {
        var e = new Events();
        // Not disposed: the uninitialised stand-ins cannot be disposed.
        var provider = Build(e);

        var hosted = provider.GetServices<IHostedService>().ToList();

        // Construction of hosted services: connection deps and regions are
        // injected; the alarm subscribers and MOB are not built yet.
        await Assert.That(e.Log.Contains("ctor:connection-deps")).IsTrue();
        await Assert.That(e.Log.Contains("ctor:regions")).IsTrue();
        foreach (var late in new[] { "ctor:publisher", "ctor:driver", "ctor:autoadvance", "ctor:averages" })
        {
            await Assert.That(e.Log.Contains(late)).IsFalse();
        }

        e.Add("--start--");
        foreach (var h in hosted)
        {
            await h.StartAsync(CancellationToken.None);
        }

        var started = e.Log.Skip(e.Log.IndexOf("--start--") + 1).ToList();
        await Assert.That(started[0]).IsEqualTo("settings");
        await Assert.That(string.Join(",", started.Skip(1).Take(5)))
            .IsEqualTo("login,socket,refresh:startup,mob,alarms");
        // Snoozes are rehydrated before any alarm subscriber is built.
        await Assert.That(started.IndexOf("alarms")).IsLessThan(started.IndexOf("ctor:publisher"));
        foreach (var late in new[] { "ctor:publisher", "ctor:driver", "ctor:autoadvance", "ctor:averages" })
        {
            await Assert.That(started.Contains(late)).IsTrue();
        }
    }
}
