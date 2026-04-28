using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Signalk;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the design.draft REST seeder's parsing across the three
/// response shapes signalk-server has emitted in the wild
/// (canonical {value: {current, maximum}}, sibling-leaves shape,
/// bare scalars). Extracted from SignalkClient as part of ARCH-006
/// from the architecture review; the seeder is now testable in
/// isolation without spinning up the full WebSocket lifecycle.
/// </summary>
public class SignalkDraftSeederTests
{
    private sealed class FakeBaseUrl : ISignalKBaseUrl
    {
        public string BaseUrl => "http://test.local";
        public Uri StreamUri(string subscribe = "none") => new("ws://test.local");
        public string Combine(string path) => BaseUrl + path;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var res = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            };
            return Task.FromResult(res);
        }
    }

    private static (SignalkDraftSeeder seeder, NavigationData data, int onChangedCalls)
        Build(HttpStatusCode status, string body)
    {
        var http = new HttpClient(new StubHandler(status, body));
        var data = new NavigationData();
        int calls = 0;
        var seeder = new SignalkDraftSeeder(
            http, new FakeBaseUrl(), data,
            NullLogger<SignalkDraftSeeder>.Instance,
            onDataChanged: () => calls++);
        return (seeder, data, calls);
    }

    [Test]
    public async Task SeedAsync_CanonicalShape_PopulatesDraftCurrent()
    {
        // signalk-server canonical shape: { value: { current, maximum } }
        var body = """{"meta":{},"value":{"current":1.5,"maximum":2.0},"timestamp":"..."}""";
        var (seeder, data, _) = Build(HttpStatusCode.OK, body);

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(data.DraftFromSignalK).IsEqualTo(1.5);
    }

    [Test]
    public async Task SeedAsync_FallsBackToMaximum_WhenCurrentMissing()
    {
        // Older / alternate servers emit only maximum.
        var body = """{"value":{"maximum":2.0}}""";
        var (seeder, data, _) = Build(HttpStatusCode.OK, body);

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(data.DraftFromSignalK).IsEqualTo(2.0);
    }

    [Test]
    public async Task SeedAsync_RootLevelLeaves_AlsoSupported()
    {
        // Some installs emit current+maximum as root-level siblings
        // (no value envelope).
        var body = """{"current":{"value":1.2,"timestamp":"..."},"maximum":{"value":1.8,"timestamp":"..."}}""";
        var (seeder, data, _) = Build(HttpStatusCode.OK, body);

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(data.DraftFromSignalK).IsEqualTo(1.2);
    }

    [Test]
    public async Task SeedAsync_404_LeavesDraftUntouched()
    {
        // No design data on server -- silent no-op at Debug level.
        var (seeder, data, _) = Build(HttpStatusCode.NotFound, "");
        await seeder.SeedAsync(CancellationToken.None);
        await Assert.That(data.DraftFromSignalK).IsNull();
    }

    [Test]
    public async Task SeedAsync_NonObjectBody_LeavesDraftUntouched()
    {
        // Defensive: a server returning an array or a bare number
        // shouldn't crash; the seeder bails on non-object.
        var (seeder, data, _) = Build(HttpStatusCode.OK, "[1,2,3]");
        await seeder.SeedAsync(CancellationToken.None);
        await Assert.That(data.DraftFromSignalK).IsNull();
    }

    [Test]
    public async Task UnwrapValue_HandlesValueEnvelope()
    {
        var doc = System.Text.Json.JsonDocument.Parse("""{"value":1.5,"timestamp":"x"}""");
        var unwrapped = SignalkDraftSeeder.UnwrapValue(doc.RootElement);
        await Assert.That(unwrapped.GetDouble()).IsEqualTo(1.5);
    }

    [Test]
    public async Task UnwrapValue_PassesThroughBareScalar()
    {
        var doc = System.Text.Json.JsonDocument.Parse("3.14");
        var unwrapped = SignalkDraftSeeder.UnwrapValue(doc.RootElement);
        await Assert.That(unwrapped.GetDouble()).IsEqualTo(3.14);
    }
}
