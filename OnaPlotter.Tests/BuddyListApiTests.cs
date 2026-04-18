using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

public class BuddyListApiTests
{
    private static BuddyListApi NewApi(HttpClient client) =>
        new(client, ApiTestHelpers.FixedBaseUrl(), NullLogger<BuddyListApi>.Instance);

    [Test]
    public async Task GetAllAsync_404_ReturnsNull_And_NotAvailable()
    {
        int calls = 0;
        var client = ApiTestHelpers.MockClient(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var api = NewApi(client);

        var result = await api.GetAllAsync();

        await Assert.That(result).IsNull();
        await Assert.That(await api.IsAvailableAsync()).IsFalse();
        // Second IsAvailable call reuses cache - no second HTTP call.
        await api.IsAvailableAsync();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task GetAllAsync_200_ParsesEntries()
    {
        const string body = """
        {
          "urn:mrn:imo:mmsi:338246284": { "name": "Morning Star" },
          "urn:mrn:imo:mmsi:123456789": { "name": "Sea Swallow" }
        }
        """;
        var client = ApiTestHelpers.JsonClient(_ => body);
        var api = NewApi(client);

        var result = await api.GetAllAsync();

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Count).IsEqualTo(2);
        await Assert.That(await api.IsAvailableAsync()).IsTrue();

        var morning = result.First(b => b.Name == "Morning Star");
        await Assert.That(morning.Mmsi).IsEqualTo("338246284");
    }

    [Test]
    public async Task GetAllAsync_MissingName_FallsBackToUrn()
    {
        // The plugin's docs say each entry has a `name`, but we shouldn't
        // assume that - defend against malformed entries so a single bad
        // buddy doesn't break the whole list.
        const string body = """
        {
          "urn:mrn:imo:mmsi:000000001": {}
        }
        """;
        var client = ApiTestHelpers.JsonClient(_ => body);
        var api = NewApi(client);

        var result = await api.GetAllAsync();

        await Assert.That(result).IsNotNull();
        await Assert.That(result![0].Name).IsEqualTo("urn:mrn:imo:mmsi:000000001");
    }

    [Test]
    public async Task GetAllAsync_NetworkError_NotAvailable()
    {
        var client = ApiTestHelpers.MockClient(_ => throw new HttpRequestException("DNS fail"));
        var api = NewApi(client);

        var result = await api.GetAllAsync();

        await Assert.That(result).IsNull();
        await Assert.That(await api.IsAvailableAsync()).IsFalse();
    }

    [Test]
    public async Task Invalidate_ResetsCache()
    {
        int calls = 0;
        var client = ApiTestHelpers.MockClient(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var api = NewApi(client);

        await api.IsAvailableAsync();
        await api.IsAvailableAsync();
        await Assert.That(calls).IsEqualTo(1);

        api.InvalidateAsync();
        await api.IsAvailableAsync();
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task AddAsync_200_ReturnsTrue_MarksAvailable()
    {
        HttpRequestMessage? captured = null;
        var client = ApiTestHelpers.MockClient(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var api = NewApi(client);

        var ok = await api.AddAsync("urn:mrn:imo:mmsi:1234", "Test Boat");

        await Assert.That(ok).IsTrue();
        await Assert.That(await api.IsAvailableAsync()).IsTrue();
        await Assert.That(captured!.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(captured.RequestUri!.PathAndQuery).IsEqualTo("/signalk/v2/api/resources/buddies");
    }

    [Test]
    public async Task AddAsync_404_ReturnsFalse_MarksUnavailable()
    {
        var client = ApiTestHelpers.MockClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var api = NewApi(client);

        var ok = await api.AddAsync("urn:...", "Name");

        await Assert.That(ok).IsFalse();
        await Assert.That(await api.IsAvailableAsync()).IsFalse();
    }

    [Test]
    public async Task RemoveAsync_HitsCorrectEncodedUrl()
    {
        string? hitPath = null;
        HttpMethod? method = null;
        var client = ApiTestHelpers.MockClient(req =>
        {
            hitPath = req.RequestUri!.AbsoluteUri;
            method = req.Method;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = NewApi(client);

        var ok = await api.RemoveAsync("urn:mrn:imo:mmsi:338 246 284");

        await Assert.That(ok).IsTrue();
        await Assert.That(method).IsEqualTo(HttpMethod.Delete);
        // The URN embeds spaces (or any char) - must be percent-encoded.
        await Assert.That(hitPath).Contains("338%20246%20284");
    }

    [Test]
    public async Task GetAllAsync_HitsBuddiesPath()
    {
        string? hitPath = null;
        var client = ApiTestHelpers.MockClient(req =>
        {
            hitPath = req.RequestUri!.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var api = NewApi(client);

        await api.GetAllAsync();

        await Assert.That(hitPath).IsEqualTo("/signalk/v2/api/resources/buddies");
    }
}
