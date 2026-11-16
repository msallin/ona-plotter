using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.State;

namespace OnaPlotter.Tests;

/// <summary>
/// Every service reads "now" from its injected <see cref="TimeProvider"/>,
/// so a fake clock fully controls stamps, expiry and defaults.
/// </summary>
public class ServiceClockTests
{
    private static FakeTimeProvider NewClock() => new(new DateTimeOffset(TestClock.FixedUtcNow));

    [Test]
    public async Task DeadmanTracker_StartsAtConstruction_AndTouchStampsClock()
    {
        var clock = NewClock();
        var tracker = new DeadmanTracker(clock);
        await Assert.That(tracker.LastInteractionUtc).IsEqualTo(TestClock.FixedUtcNow);

        clock.Advance(TimeSpan.FromMinutes(7));
        tracker.Touch();
        await Assert.That(tracker.LastInteractionUtc).IsEqualTo(TestClock.FixedUtcNow.AddMinutes(7));
    }

    [Test]
    public async Task ToastService_ExpiresOnTheInjectedClock_PinnedStays()
    {
        var clock = NewClock();
        var toasts = new ToastService(clock);
        toasts.Show("short", ToastLevel.Info, durationSec: 4);
        toasts.Pinned("MOB 47 30.000N");

        clock.Advance(TimeSpan.FromSeconds(3.9));
        await Assert.That(toasts.Active.Count).IsEqualTo(2);

        clock.Advance(TimeSpan.FromSeconds(0.1));
        await Assert.That(toasts.Active.Select(t => t.Message)).IsEquivalentTo(new[] { "MOB 47 30.000N" });
    }

    [Test]
    public async Task TrackBuffer_DepthTrend_WindowedOnTheInjectedClock()
    {
        var clock = NewClock();
        var track = new TrackBuffer(clock);
        TrackPoint At(int minutesAgo, double depth) => new(
            TestClock.FixedUtcNow.AddMinutes(-minutesAgo), 0, 0, null, null, null,
            null, null, null, null, depth, null);
        track.Add(At(20, 10));   // outside a 10-minute window
        track.Add(At(10, 8));
        track.Add(At(0, 6));

        var trend = track.GetDepthTrendMetersPerMinute(TimeSpan.FromMinutes(10));

        await Assert.That(trend).IsEqualTo(-0.2);
    }

    [Test]
    public async Task PageStates_DefaultWindows_FromTheLocalClock()
    {
        var clock = NewClock();
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("Test+02", TimeSpan.FromHours(2), "Test+02", "Test+02"));
        var localNow = new DateTime(2025, 1, 1, 14, 0, 0, DateTimeKind.Local);

        var history = new HistoryPageState(clock);
        var stats = new StatsPageState(clock);

        await Assert.That(history.CustomToLocal).IsEqualTo(localNow);
        await Assert.That(history.CustomFromLocal).IsEqualTo(localNow.AddHours(-6));
        await Assert.That(stats.CustomToLocal).IsEqualTo(localNow);
        await Assert.That(stats.CustomFromLocal).IsEqualTo(localNow.Date.AddDays(-7));
    }

    [Test]
    public async Task NoteApi_Create_StampsCreatedAtFromClock()
    {
        string? body = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            body = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\"n1\"") };
        });
        var api = new NoteApi(http, ApiTestHelpers.FixedBaseUrl(), NewClock(), NullLogger<NoteApi>.Instance);

        await api.CreateAsync("Anchorage", "Good holding", 47.0, 8.0);

        await Assert.That(body).Contains("\"createdAt\":\"2025-01-01T12:00:00.0000000Z\"");
    }

    [Test]
    public async Task TrackApi_FromWithoutTo_EndsWindowAtClockNow()
    {
        string? url = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            url = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var api = new TrackApi(http, ApiTestHelpers.FixedBaseUrl(), NewClock(), NullLogger<TrackApi>.Instance);

        await api.GetServerTrackPointsAsync(new DateTimeOffset(TestClock.FixedUtcNow.AddHours(-1)), null, null);

        await Assert.That(url).Contains("&to=" + Uri.EscapeDataString("2025-01-01T12:00:00.0000000Z"));
    }

    [Test]
    public async Task DependencyInjection_PicksTheTimeProviderConstructors()
    {
        // Stores with a system-clock convenience constructor must still
        // get the registered TimeProvider when DI builds them, as
        // Program.cs does.
        var clock = NewClock();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<OnaPlotter.Services.Settings.IMapDisplaySettings>(new FakeSettings());
        services.AddSingleton<OnaPlotter.Services.IKeyValueStore>(new KeyValueStoreStub());
        services.AddSingleton<NavigationData>();
        services.AddSingleton<AisStore>();
        services.AddSingleton<TrackBuffer>();
        services.AddSingleton<IToastService, ToastService>();
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<DeadmanTracker>();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var nav = sp.GetRequiredService<NavigationData>();
        nav.MarkDataReceived();
        await Assert.That(nav.LastReceivedUtc).IsEqualTo(TestClock.FixedUtcNow);

        var ais = sp.GetRequiredService<AisStore>();
        ais.Apply("vessels.urn:mrn:imo:mmsi:1", "navigation.position", DeltaValue.FromPosition(1, 2));
        await Assert.That(ais.GetVessels().Single().LastAisSeen).IsEqualTo(TestClock.FixedUtcNow);

        await Assert.That(sp.GetRequiredService<DeadmanTracker>().LastInteractionUtc).IsEqualTo(TestClock.FixedUtcNow);

        var toasts = sp.GetRequiredService<IToastService>();
        toasts.Show("x", ToastLevel.Info, durationSec: 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(toasts.Active.Count).IsEqualTo(0);

        await Assert.That(sp.GetRequiredService<TrackBuffer>()).IsNotNull();
        await Assert.That(sp.GetRequiredService<AppSettingsService>()).IsNotNull();
    }

    private sealed class KeyValueStoreStub : OnaPlotter.Services.IKeyValueStore
    {
        private readonly Dictionary<string, string> _values = [];
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        { _values[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key, CancellationToken ct = default)
        { _values.Remove(key); return Task.CompletedTask; }
    }
}
