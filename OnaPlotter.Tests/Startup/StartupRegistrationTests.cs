using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Services;
using OnaPlotter.Services.Signalk;
using OnaPlotter.Services.Startup;

namespace OnaPlotter.Tests.Startup;

/// <summary>
/// Pins the registration order of the hosted services: the WASM host
/// starts them sequentially in this order, and the order encodes the
/// settings -> connection -> safety coupling.
/// </summary>
public class StartupRegistrationTests
{
    private static List<ServiceDescriptor> HostedDescriptors()
    {
        var services = new ServiceCollection();
        services.AddOnaStartup();
        return services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
    }

    [Test]
    public async Task Hosted_Services_Are_Registered_In_Startup_Order()
    {
        var descriptors = HostedDescriptors();

        await Assert.That(descriptors.Count).IsEqualTo(6);
        await Assert.That(descriptors[0].ImplementationType).IsEqualTo(typeof(SettingsBootstrap));
        await Assert.That(descriptors[1].ImplementationType).IsEqualTo(typeof(ConnectionBootstrap));
        await Assert.That(descriptors[2].ImplementationType).IsEqualTo(typeof(SafetyBootstrap));
        // UpdateChecker is a forwarder to the singleton, so it has a factory.
        await Assert.That(descriptors[3].ImplementationFactory).IsNotNull();
        // The login poller runs after SettingsBootstrap.
        await Assert.That(descriptors[4].ImplementationFactory).IsNotNull();
        await Assert.That(descriptors[5].ImplementationFactory).IsNotNull();
    }

    [Test]
    public async Task StaleDataMonitor_And_LoginStatusPoller_Hosted_Registrations_Forward_To_The_Singletons()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton<ISignalkClient>(new FakeSignalkClient());
        services.AddSingleton<OnaPlotter.Services.Api.IAuthApi>(new LoginStatusPollerTests.FakeAuthApi());
        services.AddOnaStartup();
        using var provider = services.BuildServiceProvider();
        var descriptors = HostedDescriptors();

        await Assert.That(descriptors[4].ImplementationFactory!(provider))
            .IsSameReferenceAs(provider.GetRequiredService<StaleDataMonitor>());
        await Assert.That(descriptors[5].ImplementationFactory!(provider))
            .IsSameReferenceAs(provider.GetRequiredService<OnaPlotter.Services.Auth.LoginStatusPoller>());
    }

    [Test]
    public async Task UpdateChecker_Hosted_Registration_Forwards_To_The_Singleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new HttpClient());
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton<UpdateChecker>();
        services.AddOnaStartup();
        using var provider = services.BuildServiceProvider();

        var hosted = HostedDescriptors()[3].ImplementationFactory!(provider);

        await Assert.That(hosted).IsSameReferenceAs(provider.GetRequiredService<UpdateChecker>());
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("window.OnaPlotterBuild = { hash: \"abc1234\" };"),
            });
        }
    }

    private static async Task WaitFor(Func<bool> cond)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!cond() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    [Test]
    public async Task UpdateChecker_Timer_Starts_Only_In_StartAsync_And_Only_Once()
    {
        var time = new FakeTimeProvider();
        var handler = new CountingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var checker = new UpdateChecker(http, time);

        time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(50);
        await Assert.That(handler.Requests).IsEqualTo(0);

        await checker.StartAsync(CancellationToken.None);
        await checker.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(UpdateChecker.FirstCheckSeconds + 1));
        await WaitFor(() => handler.Requests >= 1);
        await Task.Delay(50);

        await Assert.That(handler.Requests).IsEqualTo(1);
    }

    [Test]
    public async Task UpdateChecker_StopAsync_Stops_The_Timer()
    {
        var time = new FakeTimeProvider();
        var handler = new CountingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var checker = new UpdateChecker(http, time);

        await checker.StartAsync(CancellationToken.None);
        var stop = checker.StopAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(50);

        await Assert.That(stop.IsCompletedSuccessfully).IsTrue();
        await Assert.That(handler.Requests).IsEqualTo(0);
    }
}
