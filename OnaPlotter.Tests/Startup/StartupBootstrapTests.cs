using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using OnaPlotter.Services;
using OnaPlotter.Services.Startup;

namespace OnaPlotter.Tests.Startup;

/// <summary>
/// The WASM host awaits each hosted service's StartAsync before the first
/// render, so every bootstrap must return promptly and never throw on a
/// runtime failure. Kicked tasks run in the background; their faults are
/// logged as warnings.
/// </summary>
public class StartupBootstrapTests
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(2);

    private static Task Pending() => new TaskCompletionSource().Task;

    private static async Task WaitForWarning(CapturingLogger logger, int count)
    {
        var deadline = DateTime.UtcNow + Prompt;
        while (logger.Entries.Count(e => e.Level == LogLevel.Warning) < count
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task BackgroundKick_Logs_Warning_When_Work_Is_Cancelled_Or_Times_Out()
    {
        var logger = new CapturingLogger();

        BackgroundKick.Run(() => Task.FromException(new TaskCanceledException("8s timeout")), logger, "x");
        await WaitForWarning(logger, 1);

        await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(1);
    }

    [Test]
    public async Task BackgroundKick_Logs_Warning_When_Work_Throws_Synchronously()
    {
        var logger = new CapturingLogger();

        BackgroundKick.Run(() => throw new InvalidOperationException("boom"), logger, "x");
        await WaitForWarning(logger, 1);

        await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(1);
    }

    [Test]
    public async Task SafetyBootstrap_Returns_Promptly_When_Mob_Recovery_Never_Completes()
    {
        var activated = false;
        var sut = new SafetyBootstrap(Pending, () => Task.CompletedTask, () => activated = true, new CapturingLogger());

        var start = sut.StartAsync(CancellationToken.None);

        await Assert.That(start.IsCompletedSuccessfully).IsTrue();
        await Assert.That(activated).IsTrue();
    }

    [Test]
    public async Task SafetyBootstrap_Logs_Warning_And_Still_Activates_When_Mob_Recovery_Faults()
    {
        var logger = new CapturingLogger();
        var activated = false;
        var sut = new SafetyBootstrap(
            () => Task.FromException(new HttpRequestException("offline")),
            () => Task.CompletedTask,
            () => activated = true,
            logger);

        await sut.StartAsync(CancellationToken.None);
        await WaitForWarning(logger, 1);

        await Assert.That(activated).IsTrue();
        await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(1);
    }

    [Test]
    public async Task SafetyBootstrap_Activates_Subscribers_Only_After_Alarm_Rehydration()
    {
        var order = new List<string>();
        var rehydrate = new TaskCompletionSource();
        var sut = new SafetyBootstrap(
            Pending,
            () => { order.Add("rehydrate-start"); return rehydrate.Task; },
            () => order.Add("activate"),
            new CapturingLogger());

        var start = sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await Assert.That(start.IsCompleted).IsFalse();
        await Assert.That(order.Contains("activate")).IsFalse();

        rehydrate.SetResult();
        await start;

        await Assert.That(string.Join(",", order)).IsEqualTo("rehydrate-start,activate");
    }

    [Test]
    public async Task SafetyBootstrap_Logs_Warning_And_Still_Activates_When_Alarm_Rehydration_Throws()
    {
        var logger = new CapturingLogger();
        var activated = false;
        var sut = new SafetyBootstrap(
            Pending,
            () => Task.FromException(new InvalidOperationException("storage")),
            () => activated = true,
            logger);

        await sut.StartAsync(CancellationToken.None);

        await Assert.That(activated).IsTrue();
        await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(1);
    }

    [Test]
    public async Task SettingsBootstrap_Does_Not_Complete_Until_Settings_Initialised()
    {
        var init = new TaskCompletionSource();
        var settings = SettingsProxy.Create(() => init.Task);
        var sut = new SettingsBootstrap(settings);

        var start = sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await Assert.That(start.IsCompleted).IsFalse();

        init.SetResult();
        await start;
        await Assert.That(start.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task StopAsync_Completes_Immediately_For_Every_Bootstrap()
    {
        IHostedService[] all =
        [
            new SettingsBootstrap(SettingsProxy.Create(() => Task.CompletedTask)),
            new SafetyBootstrap(Pending, () => Task.CompletedTask, () => { }, new CapturingLogger()),
        ];

        foreach (var s in all)
        {
            await Assert.That(s.StopAsync(CancellationToken.None).IsCompletedSuccessfully).IsTrue();
        }
    }

    /// <summary>Minimal <see cref="IAppSettings"/> whose only live member is
    /// <c>InitializeAsync</c>.</summary>
    internal class SettingsProxy : DispatchProxy
    {
        private Func<Task> _init = () => Task.CompletedTask;

        public static IAppSettings Create(Func<Task> init)
        {
            var proxy = Create<IAppSettings, SettingsProxy>();
            ((SettingsProxy)(object)proxy)._init = init;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IAppSettings.InitializeAsync)
                ? _init()
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
