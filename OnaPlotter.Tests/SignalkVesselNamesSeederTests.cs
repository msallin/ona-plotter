using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Signalk;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the /signalk/v1/api/vessels REST seeder's tree-walk parsing
/// across the leaf shapes signalk-server emits ({value, timestamp}
/// envelope vs bare scalar) and the static-data subset
/// AisVessel.Apply understands (name / mmsi / callsign / ship type /
/// position). The seeder is best-effort: a 404 or malformed body
/// must NOT throw - vessel names just trickle in via deltas instead.
/// </summary>
public class SignalkVesselNamesSeederTests
{
    private sealed class FakeBaseUrl : ISignalKBaseUrl
    {
        public string BaseUrl => "http://test.local";
        public Uri StreamUri(string subscribe = "none") => new("ws://test.local");
        public event Action? OnBaseUrlChanged { add { } remove { } }
        public string Combine(string path) => BaseUrl + path;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _throw;

        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; _throw = null; }
        public StubHandler(Exception toThrow) { _status = HttpStatusCode.OK; _body = ""; _throw = toThrow; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_throw is not null) throw _throw;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
        }
    }

    private static (SignalkVesselNamesSeeder seeder, AisStore ais) Build(HttpStatusCode status, string body)
    {
        var ais = new AisStore();
        var seeder = new SignalkVesselNamesSeeder(
            new HttpClient(new StubHandler(status, body)),
            new FakeBaseUrl(),
            ais,
            NullLogger<SignalkVesselNamesSeeder>.Instance);
        return (seeder, ais);
    }

    [Test]
    public async Task SeedAsync_WrappedLeavesWithPosition_PopulatesNameAndCallsign()
    {
        // signalk-server canonical leaf shape: {value, timestamp}.
        // Position included so GetVessels() returns the vessel
        // (the snapshot filters out positionless entries).
        const string body = """
        {
          "urn:mrn:imo:mmsi:111111111": {
            "name": {"value":"Alpha","timestamp":"x"},
            "mmsi": {"value":"111111111","timestamp":"x"},
            "communication": {"callsignVhf": {"value":"ALPHA1","timestamp":"x"}},
            "navigation": {"position": {"value":{"latitude":50.0,"longitude":1.0}}}
          },
          "urn:mrn:imo:mmsi:222222222": {
            "name": {"value":"Bravo","timestamp":"x"},
            "navigation": {"position": {"value":{"latitude":51.0,"longitude":2.0}}}
          }
        }
        """;
        var (seeder, ais) = Build(HttpStatusCode.OK, body);

        await seeder.SeedAsync(CancellationToken.None);

        var vessels = ais.GetVessels();
        var alpha = vessels.FirstOrDefault(v => v.Context.EndsWith("111111111"));
        var bravo = vessels.FirstOrDefault(v => v.Context.EndsWith("222222222"));
        await Assert.That(alpha).IsNotNull();
        await Assert.That(alpha!.Name).IsEqualTo("Alpha");
        await Assert.That(alpha.Callsign).IsEqualTo("ALPHA1");
        await Assert.That(bravo).IsNotNull();
        await Assert.That(bravo!.Name).IsEqualTo("Bravo");
    }

    [Test]
    public async Task SeedAsync_BareLeavesWithPosition_AlsoSupported()
    {
        // Some servers emit bare scalars (no {value, timestamp}
        // envelope). UnwrapValue handles both shapes; bare position
        // objects (no value envelope) feed straight into AisVessel.Apply.
        const string body = """
        {
          "urn:mrn:imo:mmsi:333333333": {
            "name": "Charlie",
            "mmsi": "333333333",
            "navigation": {"position": {"latitude":52.0,"longitude":3.0}}
          }
        }
        """;
        var (seeder, ais) = Build(HttpStatusCode.OK, body);

        await seeder.SeedAsync(CancellationToken.None);

        var v = ais.GetVessels().Single();
        await Assert.That(v.Name).IsEqualTo("Charlie");
        await Assert.That(v.Latitude).IsEqualTo(52.0);
    }

    [Test]
    public async Task SeedAsync_PrefixedKey_NotDoubledUp()
    {
        // Defensive: if the server already prefixes keys with
        // "vessels.", we must NOT double the prefix - the resulting
        // context must match what the delta stream emits so a later
        // delta merges with the seeded vessel rather than creating a
        // second one.
        const string body = """
        {
          "vessels.urn:mrn:imo:mmsi:444444444": {
            "name": {"value":"Delta"},
            "navigation": {"position": {"value":{"latitude":53.0,"longitude":4.0}}}
          }
        }
        """;
        var (seeder, ais) = Build(HttpStatusCode.OK, body);

        await seeder.SeedAsync(CancellationToken.None);

        var v = ais.GetVessels().Single();
        await Assert.That(v.Context).IsEqualTo("vessels.urn:mrn:imo:mmsi:444444444");
    }

    [Test]
    public async Task SeedAsync_404_DoesNotPopulateAis()
    {
        // Server without the v1 vessels REST endpoint: silent no-op.
        var (seeder, ais) = Build(HttpStatusCode.NotFound, "");
        await seeder.SeedAsync(CancellationToken.None);
        await Assert.That(ais.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SeedAsync_NonObjectRoot_DoesNotPopulateAis()
    {
        // Defensive: a server returning an array shouldn't crash the
        // walk; the seeder bails on non-object root.
        var (seeder, ais) = Build(HttpStatusCode.OK, "[1,2,3]");
        await seeder.SeedAsync(CancellationToken.None);
        await Assert.That(ais.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SeedAsync_MalformedJson_DoesNotThrow()
    {
        // JsonException must not surface from SeedAsync. The catch-all
        // logs at Warning so the receive loop keeps going.
        var (seeder, ais) = Build(HttpStatusCode.OK, "{ this is not json");
        await seeder.SeedAsync(CancellationToken.None);
        await Assert.That(ais.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SeedAsync_TransportException_LogsButDoesNotThrow()
    {
        // Network failure / TLS error must not crash the receive loop.
        var ais = new AisStore();
        var seeder = new SignalkVesselNamesSeeder(
            new HttpClient(new StubHandler(new HttpRequestException("boom"))),
            new FakeBaseUrl(),
            ais,
            NullLogger<SignalkVesselNamesSeeder>.Instance);

        await seeder.SeedAsync(CancellationToken.None);
        await Assert.That(ais.Count).IsEqualTo(0);
    }
}
