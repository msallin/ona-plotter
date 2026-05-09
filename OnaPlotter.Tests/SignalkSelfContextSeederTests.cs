using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Signalk;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the /signalk/v1/api/self REST seeder's parsing across the
/// shapes signalk-server emits in the wild (JSON-quoted URN, raw URN,
/// 404 on misconfigured installs). The seeder is best-effort: a
/// missing endpoint must NOT throw or invoke the callback so the
/// receive loop can keep going on servers that don't expose it.
/// </summary>
public class SignalkSelfContextSeederTests
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

    private static (SignalkSelfContextSeeder seeder, List<string?> calls)
        Build(HttpStatusCode status, string body)
    {
        var calls = new List<string?>();
        var seeder = new SignalkSelfContextSeeder(
            new HttpClient(new StubHandler(status, body)),
            new FakeBaseUrl(),
            NullLogger<SignalkSelfContextSeeder>.Instance,
            setSelfContext: raw => calls.Add(raw));
        return (seeder, calls);
    }

    [Test]
    public async Task SeedAsync_QuotedUrn_StripsQuotesAndCallsBack()
    {
        // Canonical signalk-server response: a JSON-encoded string. The
        // surrounding double-quotes are part of JSON encoding and must
        // be trimmed before the URN reaches SetSelfContext.
        var (seeder, calls) = Build(HttpStatusCode.OK, "\"vessels.urn:mrn:imo:mmsi:261006533\"");

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(calls).HasSingleItem();
        await Assert.That(calls[0]).IsEqualTo("vessels.urn:mrn:imo:mmsi:261006533");
    }

    [Test]
    public async Task SeedAsync_BareUrn_ForwardedAsIs()
    {
        // Some installs return the URN without a JSON wrapper. Trim()
        // handles whitespace; .Trim('"') is a no-op on the bare form.
        var (seeder, calls) = Build(HttpStatusCode.OK, "urn:mrn:imo:mmsi:261006533");

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(calls).HasSingleItem();
        await Assert.That(calls[0]).IsEqualTo("urn:mrn:imo:mmsi:261006533");
    }

    [Test]
    public async Task SeedAsync_404_DoesNotInvokeCallback()
    {
        // No /v1/api/self endpoint - silent at Debug level, callback
        // stays untouched so SignalkClient's existing self-resolution
        // paths (hello message, vessels.* delta) remain authoritative.
        var (seeder, calls) = Build(HttpStatusCode.NotFound, "");

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task SeedAsync_EmptyBody_DoesNotInvokeCallback()
    {
        // Defensive: a 200 with an empty body shouldn't manufacture a
        // bogus self URN. Trimmed string is empty so the callback is skipped.
        var (seeder, calls) = Build(HttpStatusCode.OK, "");

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task SeedAsync_WhitespaceOnlyBody_DoesNotInvokeCallback()
    {
        // Edge case: server replies with whitespace + quotes only. The
        // trimmed result is empty; same outcome as the empty-body case.
        var (seeder, calls) = Build(HttpStatusCode.OK, "  \"\"  ");

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task SeedAsync_AlreadyCanceled_SwallowsCancellation()
    {
        // OperationCanceledException must not surface from SeedAsync;
        // the receive loop's own CancellationToken is the only owner.
        // The seeder either short-circuits before reading the body or
        // races and sees it; the contract is that it never throws.
        var (seeder, calls) = Build(HttpStatusCode.OK, "\"urn:mrn:imo:mmsi:1\"");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await seeder.SeedAsync(cts.Token);

        // calls list is observable end-state regardless of whether
        // cancellation hit before or after the body read; the test
        // exists to assert the absence of an unhandled exception.
        await Assert.That(calls.Count).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task SeedAsync_TransportException_LogsButDoesNotThrow()
    {
        // A misconfigured server / dead network / TLS failure must not
        // crash the receive loop. The seeder swallows + logs at Warning.
        var calls = new List<string?>();
        var seeder = new SignalkSelfContextSeeder(
            new HttpClient(new StubHandler(new HttpRequestException("boom"))),
            new FakeBaseUrl(),
            NullLogger<SignalkSelfContextSeeder>.Instance,
            setSelfContext: raw => calls.Add(raw));

        await seeder.SeedAsync(CancellationToken.None);

        // Callback never fires when the transport throws; the test
        // exists to assert the absence of an unhandled exception.
        await Assert.That(calls).IsEmpty();
    }
}
