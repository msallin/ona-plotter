using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Signalk;

namespace OnaPlotter.Tests.Services.Signalk;

/// <summary>
/// Pins that the connect-time seeders log through real category
/// loggers from the injected factory, so a failing REST seed is visible
/// in diagnostics under its own class name.
/// </summary>
public class SignalkConnectSeedingTests
{
    [Test]
    public async Task Ctor_CreatesOneCategoryLoggerPerSeeder()
    {
        var factory = new RecordingLoggerFactory();

        _ = new SignalkConnectSeeding(new HttpClient(), ApiTestHelpers.FixedBaseUrl(),
            new NavigationData(), new AisStore(),
            setSelfContext: _ => { }, onDataChanged: () => { }, factory);

        await Assert.That(factory.Categories).Contains(typeof(SignalkConnectSeeding).FullName!);
        await Assert.That(factory.Categories).Contains(typeof(SignalkDraftSeeder).FullName!);
        await Assert.That(factory.Categories).Contains(typeof(SignalkSelfContextSeeder).FullName!);
        await Assert.That(factory.Categories).Contains(typeof(SignalkVesselNamesSeeder).FullName!);
        await Assert.That(factory.Categories).Contains(typeof(SignalkCourseSeeder).FullName!);
    }

    [Test]
    public async Task SeedCourse_TransportFailure_LogsThroughFactoryLogger()
    {
        // Dead network at connect time: the failure reaches the log
        // sink rather than a null logger.
        var factory = new RecordingLoggerFactory();
        var http = ApiTestHelpers.MockClient(_ => throw new HttpRequestException("unreachable"));
        var seeding = new SignalkConnectSeeding(http, ApiTestHelpers.FixedBaseUrl(),
            new NavigationData(), new AisStore(),
            setSelfContext: _ => { }, onDataChanged: () => { }, factory);

        await seeding.SeedCourseAsync(CancellationToken.None);

        await Assert.That(factory.Entries.Any(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.Contains("unreachable"))).IsTrue();
    }
}
