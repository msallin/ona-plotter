using System.Net;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

internal static class ApiTestHelpers
{
    public const string TestBase = "http://test.local:3000";

    public static HttpClient MockClient(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new DelegateHandler(handler));

    public static HttpClient JsonClient(Func<HttpRequestMessage, string> bodyForRequest,
        HttpStatusCode status = HttpStatusCode.OK) =>
        MockClient(req =>
        {
            var body = bodyForRequest(req);
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        });

    public static ISignalKBaseUrl FixedBaseUrl(string baseUrl = TestBase) => new FakeBaseUrl(baseUrl);

    private sealed class FakeBaseUrl(string baseUrl) : ISignalKBaseUrl
    {
        // Read the captured primary-constructor parameter through the
        // BaseUrl property so the compiler doesn't warn about both
        // initialising state AND capturing the parameter for later use
        // (CS9124). Either-or; reading through the property is uniform.
        public string BaseUrl { get; } = baseUrl;
        public string RadarBaseUrl => BaseUrl;
        public Uri StreamUri(string subscribe = "none") => SignalKUrls.StreamWs(BaseUrl, subscribe);
        public string Combine(string path) => BaseUrl + path;
        public string CombineRadar(string path) => BaseUrl + path;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
