using System.Net;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Services;
using OnaPlotter.Services.Auth;

namespace OnaPlotter.Tests;

public class AuthHandlerTests
{
    [Test]
    public async Task Bearer_Header_Attached_When_Token_Valid()
    {
        var (handler, captured) = BuildHandler(
            tokenSetup: (store, time) => store.SetAsync(
                "jwt-good", time.GetUtcNow().UtcDateTime.AddHours(1),
                persist: false));

        using var client = new HttpClient(handler);
        var response = await client.GetAsync("http://t.local/api");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(captured.LastRequest!.Headers.Authorization?.Scheme).IsEqualTo("Bearer");
        await Assert.That(captured.LastRequest!.Headers.Authorization?.Parameter).IsEqualTo("jwt-good");
    }

    [Test]
    public async Task No_Header_When_Store_Empty()
    {
        // Same-origin install relies on browser session cookie - the
        // handler must NOT inject an empty / null Authorization header
        // (the value would be unparseable anyway). signalk-server's
        // cookie auth keeps working untouched.
        var (handler, captured) = BuildHandler(tokenSetup: null);

        using var client = new HttpClient(handler);
        await client.GetAsync("http://t.local/api");

        await Assert.That(captured.LastRequest!.Headers.Authorization).IsNull();
    }

    [Test]
    public async Task No_Header_When_Token_Within_Skew_Window()
    {
        // Token expires inside the 30 s skew window: IsValid returns
        // false, handler skips the attach. Request goes anonymous so
        // the helm sees the right 401 instead of a 401 against a
        // stale token they thought was working.
        var (handler, captured) = BuildHandler(
            tokenSetup: (store, time) => store.SetAsync(
                "jwt-aging", time.GetUtcNow().UtcDateTime.AddSeconds(10),
                persist: false));

        using var client = new HttpClient(handler);
        await client.GetAsync("http://t.local/api");

        await Assert.That(captured.LastRequest!.Headers.Authorization).IsNull();
    }

    [Test]
    public async Task Caller_Authorization_Header_Wins()
    {
        // Login itself doesn't go through the handler's auto-attach -
        // the request body carries the credentials. If the caller
        // pre-set an Authorization header (e.g. for a one-off token-
        // exchange flow), we leave it alone.
        var (handler, captured) = BuildHandler(
            tokenSetup: (store, time) => store.SetAsync(
                "jwt-good", time.GetUtcNow().UtcDateTime.AddHours(1),
                persist: false));

        using var client = new HttpClient(handler);
        var req = new HttpRequestMessage(HttpMethod.Get, "http://t.local/api");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "abc");
        await client.SendAsync(req);

        await Assert.That(captured.LastRequest!.Headers.Authorization?.Scheme).IsEqualTo("Basic");
        await Assert.That(captured.LastRequest!.Headers.Authorization?.Parameter).IsEqualTo("abc");
    }

    [Test]
    public async Task Token_Cleared_On_401_Response()
    {
        var kv = new InMemoryKv();
        var time = new FakeTimeProvider(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var store = new TokenStore(kv, time);
        await store.SetAsync("jwt-stale",
            time.GetUtcNow().UtcDateTime.AddHours(1), persist: true);

        var inner = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var handler = new AuthHandler(store) { InnerHandler = inner };

        using var client = new HttpClient(handler);
        var response = await client.GetAsync("http://t.local/api");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        // Server invalidated the session: stale local token wiped so
        // the next request goes anonymous (no doomed retry behind the
        // helm's back).
        await Assert.That(store.Token).IsNull();
        await Assert.That(kv.Get("auth.token.v1")).IsNull();
    }

    [Test]
    public async Task Non_401_Response_Leaves_Token_Intact()
    {
        var kv = new InMemoryKv();
        var time = new FakeTimeProvider(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var store = new TokenStore(kv, time);
        await store.SetAsync("jwt-good",
            time.GetUtcNow().UtcDateTime.AddHours(1), persist: false);

        var inner = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var handler = new AuthHandler(store) { InnerHandler = inner };

        using var client = new HttpClient(handler);
        await client.GetAsync("http://t.local/api");

        await Assert.That(store.Token).IsEqualTo("jwt-good");
    }

    private static (AuthHandler handler, CapturingHandler captured) BuildHandler(
        Func<TokenStore, FakeTimeProvider, Task>? tokenSetup)
    {
        var time = new FakeTimeProvider(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var store = new TokenStore(new InMemoryKv(), time);
        if (tokenSetup is not null) tokenSetup(store, time).GetAwaiter().GetResult();
        var captured = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new AuthHandler(store) { InnerHandler = captured };
        return (handler, captured);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class InMemoryKv : IKeyValueStore
    {
        private readonly Dictionary<string, string> _data = new();
        public string? Get(string key) => _data.TryGetValue(key, out var v) ? v : null;
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Get(key));
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            _data[key] = value;
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            _data.Remove(key);
            return Task.CompletedTask;
        }
    }
}
