using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the three URL-construction responsibilities of SignalKBaseUrl:
/// pick the configured server (or fall back to the page origin), strip
/// path+query to scheme://host:port, and derive ws/wss for the stream.
/// </summary>
public class SignalKBaseUrlTests
{
    private static IConfiguration Config(string? serverUrl)
    {
        var dict = new Dictionary<string, string?>();
        if (serverUrl is not null) dict["SignalK:ServerUrl"] = serverUrl;
        return new ConfigurationBuilder().AddInMemoryCollection(dict!).Build();
    }

    /// <summary>
    /// Minimal NavigationManager stub. NavigationManager is abstract with
    /// protected Initialize; call it via a derived class.
    /// </summary>
    private sealed class FakeNav : NavigationManager
    {
        public FakeNav(string baseUri, string uri) => Initialize(baseUri, uri);
    }

    [Test]
    public async Task Configured_Url_Is_Used_When_Present()
    {
        var bu = new SignalKBaseUrl(Config("https://boat.local:3000/anything"),
            new FakeNav("http://ignored.local/", "http://ignored.local/page"));

        await Assert.That(bu.BaseUrl).IsEqualTo("https://boat.local:3000");
    }

    [Test]
    public async Task Auto_Sentinel_Uses_Page_Origin()
    {
        // ServerUrl="auto" tells the webapp to serve off the page origin.
        // Common when OnaPlotter is installed as a SignalK webapp.
        var bu = new SignalKBaseUrl(Config("auto"),
            new FakeNav("https://openplotter.local/signalk-onaplotter/",
                        "https://openplotter.local/signalk-onaplotter/map"));
        await Assert.That(bu.BaseUrl).IsEqualTo("https://openplotter.local:443");
    }

    [Test]
    public async Task Empty_Or_Missing_Config_Falls_Back_To_Page_Origin()
    {
        var bu = new SignalKBaseUrl(Config(null),
            new FakeNav("http://localhost:5282/", "http://localhost:5282/map"));
        await Assert.That(bu.BaseUrl).IsEqualTo("http://localhost:5282");

        var bu2 = new SignalKBaseUrl(Config(""),
            new FakeNav("http://localhost:5282/", "http://localhost:5282/map"));
        await Assert.That(bu2.BaseUrl).IsEqualTo("http://localhost:5282");
    }

    [Test]
    public async Task Combine_Joins_Path_To_BaseUrl()
    {
        var bu = new SignalKBaseUrl(Config("http://boat.local:3000"),
            new FakeNav("http://x/", "http://x/"));
        await Assert.That(bu.Combine("/signalk/v1/api/vessels"))
            .IsEqualTo("http://boat.local:3000/signalk/v1/api/vessels");
    }

    [Test]
    public async Task StreamUri_Derives_Ws_From_Http()
    {
        var bu = new SignalKBaseUrl(Config("http://boat.local:3000"),
            new FakeNav("http://x/", "http://x/"));
        var stream = bu.StreamUri();
        await Assert.That(stream.Scheme).IsEqualTo("ws");
        await Assert.That(stream.Host).IsEqualTo("boat.local");
        await Assert.That(stream.Port).IsEqualTo(3000);
        await Assert.That(stream.AbsolutePath).IsEqualTo("/signalk/v1/stream");
    }

    [Test]
    public async Task StreamUri_Derives_Wss_From_Https()
    {
        var bu = new SignalKBaseUrl(Config("https://openplotter.local"),
            new FakeNav("http://x/", "http://x/"));
        var stream = bu.StreamUri();
        await Assert.That(stream.Scheme).IsEqualTo("wss");
    }

    [Test]
    public async Task StreamUri_Passes_Subscribe_Parameter()
    {
        // "subscribe=none" keeps the firehose off at connect; the client
        // sends explicit subscriptions after the socket is up. Must be
        // the default so a reconnect doesn't accidentally un-filter.
        var bu = new SignalKBaseUrl(Config("http://boat.local:3000"),
            new FakeNav("http://x/", "http://x/"));
        var stream = bu.StreamUri();
        await Assert.That(stream.Query).Contains("subscribe=none");

        var subscribed = bu.StreamUri("self");
        await Assert.That(subscribed.Query).Contains("subscribe=self");
    }
}
