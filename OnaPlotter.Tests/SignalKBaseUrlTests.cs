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

    [Test]
    public async Task Standalone_Mode_Off_Uses_Fallback_Origin()
    {
        // StandaloneMode=false, even with a non-empty URL, leaves the
        // bundled-webapp behaviour alone. The flag is the explicit gate.
        var settings = new FakeSettings
        {
            StandaloneMode = false,
            StandaloneServerUrl = "http://other.local:8080",
        };
        var bu = new SignalKBaseUrl(Config("http://boat.local:3000"),
            new FakeNav("http://x/", "http://x/"), settings);
        await Assert.That(bu.BaseUrl).IsEqualTo("http://boat.local:3000");
    }

    [Test]
    public async Task Standalone_Mode_On_With_Empty_Url_Falls_Back_To_Origin()
    {
        // Empty URL while the flag is on must NOT brick the app. We
        // fall through to the auto-detected origin (matches the
        // SignalKBaseUrl resolver's documented contract).
        var settings = new FakeSettings
        {
            StandaloneMode = true,
            StandaloneServerUrl = "",
        };
        var bu = new SignalKBaseUrl(Config("http://boat.local:3000"),
            new FakeNav("http://x/", "http://x/"), settings);
        await Assert.That(bu.BaseUrl).IsEqualTo("http://boat.local:3000");
    }

    [Test]
    public async Task Standalone_Mode_On_With_Valid_Url_Routes_Through_It()
    {
        var settings = new FakeSettings
        {
            StandaloneMode = true,
            StandaloneServerUrl = "https://remote.example.com:8443",
        };
        var bu = new SignalKBaseUrl(Config("http://boat.local:3000"),
            new FakeNav("http://x/", "http://x/"), settings);
        await Assert.That(bu.BaseUrl).IsEqualTo("https://remote.example.com:8443");

        // REST + WS both pick up the standalone URL (regression: before
        // the resolver routed through settings, Combine and StreamUri
        // captured the fallback at construction).
        await Assert.That(bu.Combine("/signalk/v1/api/vessels"))
            .IsEqualTo("https://remote.example.com:8443/signalk/v1/api/vessels");
        var stream = bu.StreamUri();
        await Assert.That(stream.Scheme).IsEqualTo("wss");
        await Assert.That(stream.Host).IsEqualTo("remote.example.com");
        await Assert.That(stream.Port).IsEqualTo(8443);
    }

    [Test]
    public async Task BaseUrl_Changed_Event_Fires_On_Url_Flip()
    {
        var settings = new FakeSettings
        {
            StandaloneMode = false,
            StandaloneServerUrl = "https://remote.example.com:8443",
        };
        var bu = new SignalKBaseUrl(Config("http://boat.local:3000"),
            new FakeNav("http://x/", "http://x/"), settings);
        int fired = 0;
        bu.OnBaseUrlChanged += () => fired++;

        // Flip the flag - the resolved origin should now change. The
        // SignalKBaseUrl resolver listens to OnSettingsChanged on the
        // settings instance; FakeSettings does not auto-fire from its
        // mutable properties, so drive the wire by calling Set...Async
        // (which DOES invoke OnSettingsChanged on the production
        // service) - but our Fake doesn't fire either. Drive the
        // event directly via the InvokeOnSettingsChanged hook.
        settings.StandaloneMode = true;
        settings.InvokeOnSettingsChanged();
        await Assert.That(fired).IsEqualTo(1);
        await Assert.That(bu.BaseUrl).IsEqualTo("https://remote.example.com:8443");

        // Idempotent: a settings-changed fan-out that doesn't move the
        // resolved URL must NOT re-fire the event.
        settings.InvokeOnSettingsChanged();
        await Assert.That(fired).IsEqualTo(1);
    }
}
