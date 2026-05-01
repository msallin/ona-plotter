using OnaPlotter.Services;

namespace OnaPlotter.Tests;

public class AppSettingsServiceTests
{
    [Test]
    public async Task Defaults_WhenStorageEmpty()
    {
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();

        await Assert.That(svc.NightMode).IsFalse();
        await Assert.That(svc.MapOrientation).IsEqualTo("north");
        await Assert.That(svc.FollowBoat).IsTrue();
        await Assert.That(svc.LaylinesVisible).IsFalse();
        await Assert.That(svc.DepthAlarmThreshold).IsEqualTo(3.0);
        await Assert.That(svc.CpaAlarmThreshold).IsEqualTo(0.5);
        await Assert.That(svc.WindShiftAlarmThreshold).IsEqualTo(15.0);
    }

    [Test]
    public async Task LoadsPersistedValues()
    {
        var kv = new InMemoryKv();
        await kv.SetAsync("nightMode", "true");
        await kv.SetAsync("mapOrientation", "course");
        await kv.SetAsync("depthAlarmThreshold", "5.5");

        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await Assert.That(svc.NightMode).IsTrue();
        await Assert.That(svc.MapOrientation).IsEqualTo("course");
        await Assert.That(svc.DepthAlarmThreshold).IsEqualTo(5.5);
    }

    [Test]
    public async Task SetPersists()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetNightModeAsync(true);
        await svc.SetDepthAlarmThresholdAsync(4.2);

        await Assert.That(await kv.GetAsync("nightMode")).IsEqualTo("true");
        await Assert.That(await kv.GetAsync("depthAlarmThreshold")).IsEqualTo("4.2");
    }

    // === Weather overlay opacity ===
    // Pinned because the JS-side leaflet layer + the C# slider both
    // clamp to [0.05, 0.95]. If the C# floor/ceiling drifts here, the
    // UI slider could allow a value the JS layer rejects. Ladder of
    // boundary cases follows the same shape every other "double with
    // clamp + invariant culture persistence" setting on this service
    // covers (CpaAlarmThreshold, ManualAnchorRadiusMeters, etc).

    [Test]
    public async Task WeatherOverlayOpacity_DefaultsTo0_5()
    {
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        await Assert.That(svc.WeatherOverlayOpacity).IsEqualTo(0.5);
    }

    [Test]
    public async Task SetWeatherOverlayOpacity_BelowFloor_ClampsTo0_05()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetWeatherOverlayOpacityAsync(0.0);
        await Assert.That(svc.WeatherOverlayOpacity).IsEqualTo(0.05);
        await Assert.That(await kv.GetAsync("weatherOverlayOpacity.v1")).IsEqualTo("0.05");
    }

    [Test]
    public async Task SetWeatherOverlayOpacity_AboveCeiling_ClampsTo0_95()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetWeatherOverlayOpacityAsync(1.5);
        await Assert.That(svc.WeatherOverlayOpacity).IsEqualTo(0.95);
        await Assert.That(await kv.GetAsync("weatherOverlayOpacity.v1")).IsEqualTo("0.95");
    }

    [Test]
    public async Task SetWeatherOverlayOpacity_InRange_KeepsValue_AndFires()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        int fires = 0;
        svc.OnSettingsChanged += () => fires++;

        await svc.SetWeatherOverlayOpacityAsync(0.7);
        await Assert.That(svc.WeatherOverlayOpacity).IsEqualTo(0.7);
        await Assert.That(fires).IsEqualTo(1);
    }

    [Test]
    public async Task SetWeatherOverlayOpacity_PersistsInvariantCulture()
    {
        // Same invariant-culture pin every other double setter on this
        // service has -- a de-CH / fr-FR helm flipping the slider must
        // store "0.7", not "0,7".
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetWeatherOverlayOpacityAsync(0.7);
        await Assert.That(await kv.GetAsync("weatherOverlayOpacity.v1")).IsEqualTo("0.7");
    }

    [Test]
    public async Task WeatherOverlayOpacity_GarbageStored_FallsBackToDefault()
    {
        var kv = new InMemoryKv();
        await kv.SetAsync("weatherOverlayOpacity.v1", "banana");
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await Assert.That(svc.WeatherOverlayOpacity).IsEqualTo(0.5);
    }

    [Test]
    public async Task InvariantCultureOnDoubles()
    {
        // Ensures persisted doubles use '.' regardless of OS locale.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetCpaAlarmThresholdAsync(0.75);

        var stored = await kv.GetAsync("cpaAlarmThreshold");
        await Assert.That(stored).IsEqualTo("0.75");
    }

    [Test]
    public async Task LoadDouble_AcceptsInvariantDot_RegardlessOfStoredFormat()
    {
        // Pin the LOAD direction (the SetCpaAlarmThresholdAsync test
        // above already pins the SAVE direction). A user on de-CH /
        // fr-FR running an older buggy build might have a "0,5" in
        // localStorage; the current loader uses InvariantCulture and
        // must fall back to the default rather than parse a comma as
        // a thousands separator and produce a nonsense threshold.
        var kv = new InMemoryKv();
        await kv.SetAsync("cpaAlarmThreshold", "0.42");          // canonical
        await kv.SetAsync("guardZoneLookaheadMinutes", "12,5");  // comma locale
        await kv.SetAsync("windShiftAlarmThreshold", "garbage"); // corrupt

        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await Assert.That(svc.CpaAlarmThreshold).IsEqualTo(0.42);
        // Comma-formatted value rejected -> default applied
        await Assert.That(svc.GuardZoneLookaheadMinutes).IsEqualTo(10.0);
        // Garbage rejected -> default applied
        await Assert.That(svc.WindShiftAlarmThreshold).IsEqualTo(15.0);
    }

    [Test]
    public async Task LastManualNightToggleUtc_RoundTripsFromIsoString()
    {
        // Regression test for the ConflictingDateTimeRoundtripStyles
        // ArgumentException that crashed boot the moment the user had
        // tapped Night mode at least once before. The old parser
        // combined RoundtripKind | AssumeUniversal which the runtime
        // rejects -- the fix uses RoundtripKind alone and forces Utc
        // when the parsed Kind is Unspecified. This test pins both
        // behaviours: a normal "o"-format Z-suffixed string and a
        // legacy stored value with no kind information must both
        // load without throwing and resolve to UTC.
        var kv = new InMemoryKv();
        var stamp = new DateTime(2026, 4, 24, 22, 30, 0, DateTimeKind.Utc);
        await kv.SetAsync("lastManualNightToggle.v1",
            stamp.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await Assert.That(svc.LastManualNightToggleUtc).IsEqualTo(stamp);
    }

    [Test]
    public async Task LastManualNightToggleUtc_DegenerateYearTreatedAsMissing()
    {
        // Degenerate values (DateTime.MinValue, year 0001, etc.) can
        // round-trip through TryParse as legitimate dates but blow up
        // downstream arithmetic -- e.g. the night-mode 12-hour manual-
        // override window does (now - lastToggle).TotalHours and a
        // year-0001 stamp produces a 17 million hour interval that
        // NEVER falls inside the override window. The loader should
        // treat anything pre-2020 as missing so the auto-night flow
        // resumes normal operation rather than locking itself out.
        var kv = new InMemoryKv();
        await kv.SetAsync("lastManualNightToggle.v1", "0001-01-01T00:00:00Z");

        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await Assert.That(svc.LastManualNightToggleUtc).IsNull();
    }

    [Test]
    public async Task LastManualNightToggleUtc_FarFutureYearTreatedAsMissing()
    {
        // Symmetric guard for the upper end -- a malformed payload
        // with year 9999 would also break "is it recent" checks. The
        // device clock is the bound: anything beyond 2100 is almost
        // certainly storage corruption.
        var kv = new InMemoryKv();
        await kv.SetAsync("lastManualNightToggle.v1", "9999-12-31T23:59:00Z");

        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await Assert.That(svc.LastManualNightToggleUtc).IsNull();
    }

    [Test]
    public async Task LastManualNightToggleUtc_LegacyUnspecifiedTreatedAsUtc()
    {
        // Hand-edited / pre-fix localStorage values may lack a kind
        // suffix. The loader should treat those as UTC rather than
        // shifting them by the browser's TZ offset (which is what
        // ToUniversalTime would otherwise do for an Unspecified
        // DateTime, since it falls back to "assume Local").
        var kv = new InMemoryKv();
        await kv.SetAsync("lastManualNightToggle.v1", "2026-04-24T22:30:00");

        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        var expected = new DateTime(2026, 4, 24, 22, 30, 0, DateTimeKind.Utc);
        await Assert.That(svc.LastManualNightToggleUtc).IsEqualTo(expected);
    }

    [Test]
    public async Task OnSettingsChanged_Fires()
    {
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        int calls = 0;
        svc.OnSettingsChanged += () => calls++;

        await svc.SetNightModeAsync(true);
        await svc.SetDepthAlarmThresholdAsync(6);

        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task EnabledCharts_RoundTrip()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetEnabledChartsAsync(["OSM", "OpenSeaMap", "navionics_x"]);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();

        await Assert.That(svc2.EnabledChartIds.Count).IsEqualTo(3);
        await Assert.That(svc2.EnabledChartIds.Contains("OpenSeaMap")).IsTrue();
        await Assert.That(svc2.EnabledChartIds.Contains("navionics_x")).IsTrue();
    }

    [Test]
    public async Task EnabledCharts_Empty_LoadsEmpty()
    {
        // Empty stored value should not produce a single empty-string element.
        var kv = new InMemoryKv();
        await kv.SetAsync("enabledChartIds", "");

        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await Assert.That(svc.EnabledChartIds.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Theme_DefaultsToSystem()
    {
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        await Assert.That(svc.Theme).IsEqualTo("system");
    }

    [Test]
    public async Task Theme_RoundTrips()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetThemeAsync("light");

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.Theme).IsEqualTo("light");
    }

    [Test]
    public async Task Theme_GarbageFallsBackToSystem()
    {
        var kv = new InMemoryKv();
        await kv.SetAsync("theme", "banana");
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await Assert.That(svc.Theme).IsEqualTo("system");
    }

    [Test]
    public async Task WindShiftLookback_RoundTrip()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetWindShiftLookbackMinutesAsync(2);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.WindShiftLookbackMinutes).IsEqualTo(2);
    }

    [Test]
    public async Task GuardZoneLookahead_RoundTrip()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetGuardZoneLookaheadMinutesAsync(15);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.GuardZoneLookaheadMinutes).IsEqualTo(15);
    }

    [Test]
    public async Task EnabledRoutes_Overwrite()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetEnabledRoutesAsync(["a", "b"]);
        await svc.SetEnabledRoutesAsync(["c"]);

        await Assert.That(svc.EnabledRouteIds.Count).IsEqualTo(1);
        await Assert.That(svc.EnabledRouteIds.Contains("c")).IsTrue();
    }

    [Test]
    public async Task ConcurrentInitialize_RunsOnce()
    {
        // Race condition test: many pages call InitializeAsync simultaneously.
        var kv = new CountingKv();
        var svc = new AppSettingsService(kv);

        var tasks = Enumerable.Range(0, 20).Select(_ => svc.InitializeAsync()).ToArray();
        await Task.WhenAll(tasks);

        // Each key should have been read exactly once, not 20 times.
        await Assert.That(kv.GetCount("nightMode")).IsEqualTo(1);
    }

    private sealed class InMemoryKv : IKeyValueStore
    {
        private readonly Dictionary<string, string> _d = [];
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_d.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        { _d[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key, CancellationToken ct = default)
        { _d.Remove(key); return Task.CompletedTask; }
    }

    private sealed class CountingKv : IKeyValueStore
    {
        private readonly Dictionary<string, int> _counts = [];
        public int GetCount(string key) => _counts.GetValueOrDefault(key, 0);
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            _counts[key] = _counts.GetValueOrDefault(key, 0) + 1;
            return Task.FromResult<string?>(null);
        }
        public Task SetAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    // --- Sailing mode ----------------------------------------------

    [Test]
    public async Task SailingMode_DefaultsToCruise()
    {
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        await Assert.That(svc.SailingMode).IsEqualTo("cruise");
    }

    [Test]
    [Arguments("cruise")]
    [Arguments("race")]
    public async Task SailingMode_RoundTripsKnownValues(string mode)
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetSailingModeAsync(mode);
        await Assert.That(await kv.GetAsync("sailingMode")).IsEqualTo(mode);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.SailingMode).IsEqualTo(mode);
    }

    [Test]
    [Arguments("Race")]        // wrong case
    [Arguments("CRUISE")]
    [Arguments("fishing")]     // valid nautical concept, not in our enum
    [Arguments("")]
    [Arguments("  ")]
    [Arguments("'; drop table --")] // absurd but cheap insurance
    public async Task SailingMode_RejectsUnknownValues_FallsBackToCruise(string input)
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetSailingModeAsync(input);

        await Assert.That(svc.SailingMode).IsEqualTo("cruise");
    }

    [Test]
    public async Task SailingMode_MalformedStoredValue_FallsBackToCruise()
    {
        // Storage was poked with a bad value (corrupted localStorage,
        // an older build's enum, a manual edit). Loading it must not
        // throw and must pick a safe default.
        var kv = new InMemoryKv();
        await kv.SetAsync("sailingMode", "supersail3000");
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await Assert.That(svc.SailingMode).IsEqualTo("cruise");
    }

    [Test]
    public async Task ChartOrder_RoundTrip_Preserves_Order()
    {
        // Unlike EnabledChartIds (HashSet; order is not meaningful),
        // ChartOrder is an ordered list because the user explicitly
        // asked for a specific draw stack. Reload has to preserve it.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetChartOrderAsync(["base", "seamap", "harbour"]);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();

        await Assert.That(svc2.ChartOrder.Count).IsEqualTo(3);
        await Assert.That(svc2.ChartOrder[0]).IsEqualTo("base");
        await Assert.That(svc2.ChartOrder[1]).IsEqualTo("seamap");
        await Assert.That(svc2.ChartOrder[2]).IsEqualTo("harbour");
    }

    [Test]
    public async Task ChartOrder_Dedupes_And_Drops_Empty()
    {
        // Defensive: a reorder path with duplicates or a stray empty id
        // would otherwise persist garbage. Dedupe on write so reload is
        // deterministic.
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();

        await svc.SetChartOrderAsync(["a", "b", "a", "", "c", "b"]);

        await Assert.That(svc.ChartOrder.Count).IsEqualTo(3);
        await Assert.That(svc.ChartOrder[0]).IsEqualTo("a");
        await Assert.That(svc.ChartOrder[1]).IsEqualTo("b");
        await Assert.That(svc.ChartOrder[2]).IsEqualTo("c");
    }

    [Test]
    public async Task ChartOrder_Empty_By_Default()
    {
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        await Assert.That(svc.ChartOrder.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ChartOrder_Append_Does_Not_Wipe_Existing()
    {
        // Regression guard for the reorder-silently-fails bug:
        //   Settings.ChartOrder.Append(x) returns an IEnumerable<string>
        //   that references _chartOrder. SetChartOrderAsync used to
        //   .Clear() _chartOrder before iterating, so the enumerator
        //   saw an empty source and yielded only the newly-appended id.
        //   Net effect: every ToggleChart(true) reset ChartOrder to
        //   just the last-toggled chart, then subsequent reorders
        //   couldn't find neighbouring ids and the swap silently failed.
        // This test passes Settings.ChartOrder.Append(x) straight to
        // SetChartOrderAsync -- exactly the pattern Map.ToggleChart
        // uses -- and asserts the accumulated list is preserved.
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();

        await svc.SetChartOrderAsync(["a"]);
        await svc.SetChartOrderAsync(svc.ChartOrder.Append("b"));
        await svc.SetChartOrderAsync(svc.ChartOrder.Append("c"));

        await Assert.That(svc.ChartOrder.Count).IsEqualTo(3);
        await Assert.That(svc.ChartOrder[0]).IsEqualTo("a");
        await Assert.That(svc.ChartOrder[1]).IsEqualTo("b");
        await Assert.That(svc.ChartOrder[2]).IsEqualTo("c");
    }

    [Test]
    public async Task SetEnabledCharts_Append_Does_Not_Wipe_Existing()
    {
        // Same aliasing footgun applies to HashSet-backed setters.
        // Defensive materialise is in place; this test pins it.
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();

        await svc.SetEnabledChartsAsync(["a"]);
        await svc.SetEnabledChartsAsync(svc.EnabledChartIds.Append("b"));

        await Assert.That(svc.EnabledChartIds.Count).IsEqualTo(2);
        await Assert.That(svc.EnabledChartIds.Contains("a")).IsTrue();
        await Assert.That(svc.EnabledChartIds.Contains("b")).IsTrue();
    }

    [Test]
    public async Task KeepScreenAwake_DefaultsTrue()
    {
        // Default ON: a plotter going to sleep mid-watch is a safety
        // regression. Opt-out, not opt-in.
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        await Assert.That(svc.KeepScreenAwake).IsTrue();
    }

    [Test]
    public async Task KeepScreenAwake_RoundTrip()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetKeepScreenAwakeAsync(false);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.KeepScreenAwake).IsFalse();
    }

    // --- MapView persistence ------------------------------------------

    [Test]
    public async Task MapView_DefaultsToNull_WhenStorageEmpty()
    {
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        await Assert.That(svc.MapViewLat).IsNull();
        await Assert.That(svc.MapViewLon).IsNull();
        await Assert.That(svc.MapViewZoom).IsNull();
    }

    [Test]
    public async Task MapView_RoundTrip()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetMapViewAsync(47.5231, -122.6402, 14);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.MapViewLat).IsEqualTo(47.5231);
        await Assert.That(svc2.MapViewLon).IsEqualTo(-122.6402);
        await Assert.That(svc2.MapViewZoom).IsEqualTo(14);
    }

    [Test]
    public async Task MapView_GarbageLogsAndDefaultsNull()
    {
        // Pragmatic policy: a corrupt payload shouldn't crash or block
        // the app; logger prints a warning and the map falls back to
        // its own defaults.
        var kv = new InMemoryKv();
        await kv.SetAsync("mapView.v1", "not-a-tuple");
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await Assert.That(svc.MapViewLat).IsNull();
        await Assert.That(svc.MapViewLon).IsNull();
        await Assert.That(svc.MapViewZoom).IsNull();
    }

    [Test]
    public async Task MapView_OutOfRange_DefaultsNull()
    {
        // Latitude clearly outside [-90, 90] -> treat as corrupt.
        var kv = new InMemoryKv();
        await kv.SetAsync("mapView.v1", "999.0|0.0|5");
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await Assert.That(svc.MapViewLat).IsNull();
    }

    [Test]
    public async Task MapView_InvariantCulture()
    {
        // Persisted doubles must use '.' separators so a round-trip
        // works on any OS locale.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetMapViewAsync(47.5, -122.25, 12);

        var stored = await kv.GetAsync("mapView.v1");
        await Assert.That(stored).IsEqualTo("47.5|-122.25|12");
    }

    // --- Mobile first-run sidebar default ----------------------------

    [Test]
    public async Task ApplyMobileFirstRunDefaults_OnPhoneFirstRun_CollapsesAndPersists()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.ApplyMobileFirstRunDefaultsAsync(isMobile: true);

        await Assert.That(svc.SidebarCollapsed).IsTrue();
        await Assert.That(await kv.GetAsync("sidebarCollapsed.v1")).IsEqualTo("true");
    }

    [Test]
    public async Task ApplyMobileFirstRunDefaults_OnDesktopFirstRun_LeavesExpanded()
    {
        // Same fresh-init flow but isMobile=false: should be a no-op so
        // the desktop user lands in the expanded rail.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.ApplyMobileFirstRunDefaultsAsync(isMobile: false);

        await Assert.That(svc.SidebarCollapsed).IsFalse();
        await Assert.That(await kv.GetAsync("sidebarCollapsed.v1")).IsNull();
    }

    [Test]
    public async Task ApplyMobileFirstRunDefaults_RespectsExplicitFalse()
    {
        // Helm has previously expanded the rail on phone (stored false).
        // The mobile auto-default must not flip it back to collapsed --
        // explicit user choice wins.
        var kv = new InMemoryKv();
        await kv.SetAsync("sidebarCollapsed.v1", "false");
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.ApplyMobileFirstRunDefaultsAsync(isMobile: true);

        await Assert.That(svc.SidebarCollapsed).IsFalse();
        await Assert.That(await kv.GetAsync("sidebarCollapsed.v1")).IsEqualTo("false");
    }

    [Test]
    public async Task ApplyMobileFirstRunDefaults_RespectsExplicitTrue()
    {
        // Symmetric counterpart: stored true stays true regardless of
        // viewport hint.
        var kv = new InMemoryKv();
        await kv.SetAsync("sidebarCollapsed.v1", "true");
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.ApplyMobileFirstRunDefaultsAsync(isMobile: false);

        await Assert.That(svc.SidebarCollapsed).IsTrue();
        await Assert.That(await kv.GetAsync("sidebarCollapsed.v1")).IsEqualTo("true");
    }

    [Test]
    public async Task ApplyMobileFirstRunDefaults_IsIdempotent()
    {
        // Calling twice on phone width must not re-fire OnSettingsChanged
        // or re-write the same value -- the second call sees the
        // explicit flag now true and bails out.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        int fires = 0;
        svc.OnSettingsChanged += () => fires++;

        await svc.ApplyMobileFirstRunDefaultsAsync(isMobile: true);
        await svc.ApplyMobileFirstRunDefaultsAsync(isMobile: true);

        await Assert.That(svc.SidebarCollapsed).IsTrue();
        await Assert.That(fires).IsEqualTo(1);
    }

    [Test]
    public async Task SetSidebarCollapsed_StampsExplicit_BlocksLaterMobileDefault()
    {
        // User toggled the rail manually (explicit). A later
        // ApplyMobileFirstRunDefaults must not overwrite it, even
        // though the stored value happens to match what the auto-
        // default would set.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetSidebarCollapsedAsync(false);
        await svc.ApplyMobileFirstRunDefaultsAsync(isMobile: true);

        await Assert.That(svc.SidebarCollapsed).IsFalse();
    }

    // --- QuickBarChartIds: seeded on first load, persisted thereafter ---

    [Test]
    public async Task QuickBarChartIds_AbsentKey_SeededFromEnabledCharts()
    {
        // First-load-after-upgrade migration: the quickBarChartIds.v1
        // key didn't exist in older builds. The loader must seed it
        // from EnabledChartIds so existing users keep their chart
        // shortcuts in the quick bar; without the seed every helm
        // would land on an empty quick bar after the upgrade.
        var kv = new InMemoryKv();
        await kv.SetAsync("enabledChartIds", "OSM\nOpenSeaMap");
        // Note: no quickBarChartIds.v1 key.

        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await Assert.That(svc.QuickBarChartIds.Count).IsEqualTo(2);
        await Assert.That(svc.QuickBarChartIds.Contains("OSM")).IsTrue();
        await Assert.That(svc.QuickBarChartIds.Contains("OpenSeaMap")).IsTrue();
        // The seed should also be persisted so a subsequent load
        // doesn't re-seed (which would silently un-do user removals).
        await Assert.That(await kv.GetAsync("quickBarChartIds.v1")).IsNotNull();
    }

    [Test]
    public async Task QuickBarChartIds_PresentKey_LoadsAsIs_EvenIfDifferentFromEnabled()
    {
        // After the migration has run, the quick bar is independent.
        // A user who removed a chart from the quick bar but kept it
        // enabled overall must see that choice respected on next load.
        var kv = new InMemoryKv();
        await kv.SetAsync("enabledChartIds", "OSM\nOpenSeaMap\nNavionics");
        await kv.SetAsync("quickBarChartIds.v1", "OSM");

        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await Assert.That(svc.QuickBarChartIds.Count).IsEqualTo(1);
        await Assert.That(svc.QuickBarChartIds.Contains("OSM")).IsTrue();
    }

    [Test]
    public async Task QuickBarChartIds_PresentEmptyKey_StaysEmpty()
    {
        // Boundary: a user who explicitly cleared the quick bar
        // persists an empty value. The loader must not "helpfully"
        // re-seed from EnabledChartIds and undo the clear.
        var kv = new InMemoryKv();
        await kv.SetAsync("enabledChartIds", "OSM\nOpenSeaMap");
        await kv.SetAsync("quickBarChartIds.v1", "");

        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await Assert.That(svc.QuickBarChartIds.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetQuickBarChartsAsync_RoundTrip_AndFiresOnSettingsChanged()
    {
        // The quick-bar setter is on the OnSettingsChanged hot path
        // because the toolbar live-updates when the helm toggles a
        // chart's quick-bar membership. Pin both the persistence and
        // the event so a later refactor doesn't silently remove the
        // event hook (which is what made the toolbar stop updating
        // when the quick-bar set changes).
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        int fires = 0;
        svc.OnSettingsChanged += () => fires++;

        await svc.SetQuickBarChartsAsync(["A", "B", "C"]);

        await Assert.That(svc.QuickBarChartIds.Count).IsEqualTo(3);
        await Assert.That(fires).IsEqualTo(1);

        // Reload: persisted set comes back unchanged.
        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.QuickBarChartIds.Count).IsEqualTo(3);
    }

    [Test]
    public async Task SetQuickBarChartsAsync_Append_DoesNotWipeExisting()
    {
        // Same aliasing footgun as ChartOrder / EnabledCharts: the
        // setter must materialise the input before clearing the
        // backing collection. A caller passing
        //   svc.SetQuickBarChartsAsync(svc.QuickBarChartIds.Append("x"))
        // must end up with the existing entries plus x, not just x.
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        await svc.SetQuickBarChartsAsync(["a"]);
        await svc.SetQuickBarChartsAsync(svc.QuickBarChartIds.Append("b"));
        await Assert.That(svc.QuickBarChartIds.Count).IsEqualTo(2);
    }

    // --- Round-trip + persistence: the rest of the boolean / scalar setters ---
    // These are individually tiny but each touches a setter+reload
    // pair that wasn't exercised before. Grouping them keeps the
    // file readable. The use case being protected: a future setter
    // that forgets to call Save() or OnSettingsChanged would surface
    // here (no persistence) or fail the per-test event count.

    [Test]
    public async Task SetWindHeroMode_RoundTripsAndFires()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        int fires = 0;
        svc.OnSettingsChanged += () => fires++;

        await svc.SetWindHeroModeAsync("true");

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.WindHeroMode).IsEqualTo("true");
        await Assert.That(fires).IsEqualTo(1);
    }

    [Test]
    public async Task SetWindHeroMode_RejectsUnknownAndFallsBackToApparent()
    {
        // The normaliser allows only "apparent" | "true". A garbage
        // input must collapse to the safe default rather than poison
        // localStorage with a value the renderer can't draw against.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetWindHeroModeAsync("relative");
        await Assert.That(svc.WindHeroMode).IsEqualTo("apparent");
    }

    [Test]
    public async Task SetWindPageCompact_RoundTrips()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetWindPageCompactAsync(true);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.WindPageCompact).IsTrue();
    }

    [Test]
    [Arguments("soft")]
    [Arguments("amber")]
    [Arguments("red")]
    public async Task NightModePreset_RoundTripsKnownValues(string preset)
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetNightModePresetAsync(preset);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.NightModePreset).IsEqualTo(preset);
    }

    [Test]
    public async Task NightModePreset_RejectsUnknownAndFallsBackToSoft()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetNightModePresetAsync("neon");
        await Assert.That(svc.NightModePreset).IsEqualTo("soft");
    }

    [Test]
    public async Task SetMapOrientation_RoundTrips_AndFires()
    {
        // MapOrientation steers the rotation of the chart canvas; the
        // event fire matters because the renderer subscribes and
        // re-projects on every change.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        int fires = 0;
        svc.OnSettingsChanged += () => fires++;

        await svc.SetMapOrientationAsync("course");

        await Assert.That(svc.MapOrientation).IsEqualTo("course");
        await Assert.That(await kv.GetAsync("mapOrientation")).IsEqualTo("course");
        await Assert.That(fires).IsEqualTo(1);
    }

    [Test]
    public async Task SetFollowBoat_AndLaylinesVisible_RoundTrip()
    {
        // Pair test for two persistence-only setters (no event fire by
        // design -- both are observed via @bind in their consuming
        // pages, not via OnSettingsChanged).
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetFollowBoatAsync(false);
        await svc.SetLaylinesVisibleAsync(true);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.FollowBoat).IsFalse();
        await Assert.That(svc2.LaylinesVisible).IsTrue();
    }

    [Test]
    public async Task SetAtonsVisible_RoundTrips_AndFires()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        int fires = 0;
        svc.OnSettingsChanged += () => fires++;

        await svc.SetAtonsVisibleAsync(false);

        await Assert.That(svc.AtonsVisible).IsFalse();
        await Assert.That(fires).IsEqualTo(1);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.AtonsVisible).IsFalse();
    }

    [Test]
    public async Task SetHarborMode_NotPersisted_FreshLoadResets()
    {
        // Documented invariant: Harbor mode is in-memory only because
        // a forgotten Harbor mode silently riding into open water
        // would silence collision alarms on the next session. Pin it
        // so a "make Harbor mode persistent like everything else"
        // refactor breaks here loudly, prompting the auditor to
        // re-justify the change.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetHarborModeAsync(true);
        await Assert.That(svc.HarborMode).IsTrue();
        await Assert.That(await kv.GetAsync("harborMode")).IsNull();
        await Assert.That(await kv.GetAsync("harborMode.v1")).IsNull();

        // Fresh load -> back to default false.
        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.HarborMode).IsFalse();
    }

    [Test]
    public async Task SetHarborMode_Unchanged_DoesNotFire()
    {
        // Idempotency on the no-op path: setting Harbor mode to its
        // current value must not fire OnSettingsChanged. Without the
        // early-return guard, every NoOp tap would cascade through
        // every settings subscriber for nothing.
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        int fires = 0;
        svc.OnSettingsChanged += () => fires++;

        await svc.SetHarborModeAsync(false);   // already default
        await Assert.That(fires).IsEqualTo(0);

        await svc.SetHarborModeAsync(true);
        await Assert.That(fires).IsEqualTo(1);

        // Setting again to the same value -- no second fire.
        await svc.SetHarborModeAsync(true);
        await Assert.That(fires).IsEqualTo(1);
    }

    [Test]
    public async Task SetSnoozeDurationMinutes_ClampsInputs()
    {
        // The setter clamps to [1, 120]. Zero would snooze forever;
        // a runaway high value (negative cosmic-ray flip on a wire)
        // would silence an alarm for days after a reload.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        // Below floor.
        await svc.SetSnoozeDurationMinutesAsync(0);
        await Assert.That(svc.SnoozeDurationMinutes).IsEqualTo(1);
        await svc.SetSnoozeDurationMinutesAsync(-5);
        await Assert.That(svc.SnoozeDurationMinutes).IsEqualTo(1);

        // Above ceiling.
        await svc.SetSnoozeDurationMinutesAsync(9999);
        await Assert.That(svc.SnoozeDurationMinutes).IsEqualTo(120);

        // Inside the band -- accepted as-is.
        await svc.SetSnoozeDurationMinutesAsync(15);
        await Assert.That(svc.SnoozeDurationMinutes).IsEqualTo(15);
    }

    [Test]
    public async Task SetManualAnchorRadiusMeters_RoundTrips_InvariantCulture()
    {
        // Persisted doubles always use '.' regardless of OS locale.
        // Belt-and-braces companion to the existing CPA/Wind tests --
        // anchor radius matters at sea, where a parse-failure default
        // could double the alarm radius.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetManualAnchorRadiusMetersAsync(45.5);

        var stored = await kv.GetAsync("manualAnchorRadiusMeters.v1");
        await Assert.That(stored).IsEqualTo("45.5");

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.ManualAnchorRadiusMeters).IsEqualTo(45.5);
    }

    [Test]
    public async Task SetDeadmanTimeoutAndNight_RoundTrip()
    {
        // Both deadman knobs are persisted independently. Day-only and
        // night-only configurations need to come back unchanged so a
        // helm who set "0 day, 15 night" doesn't see one of them flip
        // to a default after a reload.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetDeadmanTimeoutMinutesAsync(0);
        await svc.SetDeadmanNightMinutesAsync(20);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.DeadmanTimeoutMinutes).IsEqualTo(0);
        await Assert.That(svc2.DeadmanNightMinutes).IsEqualTo(20);
    }

    [Test]
    public async Task SetWaypointArrivalRadius_RoundTrips()
    {
        // The arrival-radius drives the APPROACH alarm; persistence
        // must round-trip so a single helm-tweaked value stays put.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetWaypointArrivalRadiusMetersAsync(75);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.WaypointArrivalRadiusMeters).IsEqualTo(75);
    }

    [Test]
    public async Task PreferMagneticHeadingAndCourse_RoundTrip()
    {
        // Compass / GPS preferences feed back into NavigationData.
        // Two independent toggles to round-trip, both starting false.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetPreferMagneticHeadingAsync(true);
        await svc.SetPreferMagneticCourseAsync(true);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.PreferMagneticHeading).IsTrue();
        await Assert.That(svc2.PreferMagneticCourse).IsTrue();
    }

    [Test]
    public async Task AutoAdvanceWaypoints_DefaultsTrue_RoundTrips()
    {
        // Auto-advance default ON: the documented chartplotter UX is
        // "on a leg crossing, jump to the next waypoint". A regression
        // that flipped the default would silently remove the helm's
        // expected behaviour.
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        await Assert.That(svc.AutoAdvanceWaypoints).IsTrue();

        var kv = new InMemoryKv();
        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await svc2.SetAutoAdvanceWaypointsAsync(false);

        var svc3 = new AppSettingsService(kv);
        await svc3.InitializeAsync();
        await Assert.That(svc3.AutoAdvanceWaypoints).IsFalse();
    }

    [Test]
    public async Task BigType_ExpandAllHud_ShowKeyboardHints_RoundTrip()
    {
        // Cluster of accessibility / power-user toggles. Each is its
        // own setter; bundle them so a later wire-up regression
        // (e.g. a copy-paste setter using the wrong key string)
        // surfaces as a single test failure.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetBigTypeAsync(true);
        await svc.SetExpandAllHudAsync(true);
        await svc.SetShowKeyboardHintsAsync(true);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.BigType).IsTrue();
        await Assert.That(svc2.ExpandAllHud).IsTrue();
        await Assert.That(svc2.ShowKeyboardHints).IsTrue();
    }

    [Test]
    public async Task ShowAutopilotHud_ShowRadarHud_RoundTrip()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetShowAutopilotHudAsync(true);
        await svc.SetShowRadarHudAsync(true);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.ShowAutopilotHud).IsTrue();
        await Assert.That(svc2.ShowRadarHud).IsTrue();
    }

    [Test]
    public async Task SetGuardZoneWarningFactor_RoundTrips()
    {
        // The guard-zone warning factor drives the warn-vs-danger CPA
        // boundary. Persisted as F2 -- pin both the format and the
        // round-trip so a refactor that swaps to G3 (introducing
        // exponential notation) doesn't quietly invalidate every
        // helm's existing setting.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetGuardZoneWarningFactorAsync(1.75);

        var stored = await kv.GetAsync("guardZoneWarningFactor");
        await Assert.That(stored).IsEqualTo("1.75");

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.GuardZoneWarningFactor).IsEqualTo(1.75);
    }

    [Test]
    public async Task SetWindShiftAlarmThreshold_RoundTrips()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetWindShiftAlarmThresholdAsync(20);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.WindShiftAlarmThreshold).IsEqualTo(20);
    }

    [Test]
    public async Task SetAnchorTideSafetyMargin_RoundTrips()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetAnchorTideSafetyMarginAsync(0.75);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.AnchorTideSafetyMargin).IsEqualTo(0.75);
    }

    // --- Storage error handling (LoadString swallow + Save swallow) ---

    [Test]
    public async Task Initialize_StorageThrows_FallsBackToDefaults()
    {
        // The store throws JSException when localStorage is disabled
        // (private browsing). Reads must swallow that and use defaults
        // so the app doesn't crash on first paint -- "missing key"
        // and "storage disabled" are the same outcome from the
        // service's perspective.
        var kv = new ThrowingKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();   // must not throw
        await Assert.That(svc.NightMode).IsFalse();
        await Assert.That(svc.DepthAlarmThreshold).IsEqualTo(3.0);
    }

    [Test]
    public async Task Save_StorageThrows_DoesNotCrash()
    {
        // Symmetric: writes must swallow JSException too (see Save()
        // catch block). The user sees nothing persisted, but the app
        // keeps running. Pin so the catch isn't accidentally narrowed.
        var kv = new ThrowingKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        // Should not throw -- the in-memory state still updates even
        // though the persisted value is lost.
        await svc.SetNightModeAsync(true);
        await Assert.That(svc.NightMode).IsTrue();
    }

    [Test]
    public async Task LoadDateTimeUtc_GarbageString_FallsBackToNull()
    {
        // Hand-edited / corrupted localStorage values shouldn't crash
        // boot. Pin the parse-failure path explicitly (the existing
        // tests cover happy + degenerate-year + far-future; this
        // covers "not a date at all").
        var kv = new InMemoryKv();
        await kv.SetAsync("lastManualNightToggle.v1", "not-a-date");
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await Assert.That(svc.LastManualNightToggleUtc).IsNull();
    }

    [Test]
    public async Task MarkManualNightToggleAsync_PersistsRoundTrippableTimestamp()
    {
        // The mark method writes the timestamp via "o" round-trip
        // format, then a fresh service must load it back. Pin both
        // halves so a setter+loader pair drift can't break the
        // 12-hour manual-override window silently.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.MarkManualNightToggleAsync();
        await Assert.That(svc.LastManualNightToggleUtc).IsNotNull();

        var stored = await kv.GetAsync("lastManualNightToggle.v1");
        await Assert.That(stored).IsNotNull();

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.LastManualNightToggleUtc).IsNotNull();
    }

    // === Chart upscale persistence ===
    // The setter clamps in C#, the JS decorator clamps in JS, the
    // Settings input clamps in HTML; this test covers the LOAD path
    // -- a corrupt localStorage entry must collapse into [0, 3].
    // Without these guards the JS decorator could see (e.g.) -1 and
    // the chart would render with a broken maxZoom calculation.

    // === Guard zone warning ring ===
    // Default true so existing installs gain the new advisory ring
    // without an opt-in step. Persistence pinned because the toggle
    // is the only path the helm has to opt out -- the value MUST
    // round-trip across reloads.

    [Test]
    public async Task GuardZoneWarningRingVisible_DefaultsTrue()
    {
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        await Assert.That(svc.GuardZoneWarningRingVisible).IsTrue();
    }

    [Test]
    public async Task GuardZoneWarningRingVisible_RoundTrips()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await svc.SetGuardZoneWarningRingVisibleAsync(false);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.GuardZoneWarningRingVisible).IsFalse();
        await Assert.That(await kv.GetAsync("guardZoneWarningRingVisible.v1"))
            .IsEqualTo("false");
    }

    [Test]
    public async Task SetGuardZoneWarningRingVisible_FiresOnSettingsChanged()
    {
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        int fires = 0;
        svc.OnSettingsChanged += () => fires++;

        await svc.SetGuardZoneWarningRingVisibleAsync(false);

        await Assert.That(fires).IsEqualTo(1);
    }

    [Test]
    public async Task GuardZoneWarningRingVisible_GarbageStored_TreatedAsFalse()
    {
        // LoadBool's contract: a stored value is interpreted as the
        // string "true" or false-otherwise. Pin: a corrupted "yes"
        // entry resolves as false. Same shape every other boolean
        // setting on the service uses, so the helm's "off" choice
        // can never be silently flipped back to on by a corrupt
        // localStorage value AND the rare "weird stored value"
        // path is documented as off.
        var kv = new InMemoryKv();
        await kv.SetAsync("guardZoneWarningRingVisible.v1", "yes");
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await Assert.That(svc.GuardZoneWarningRingVisible).IsFalse();
    }

    [Test]
    public async Task ChartUpscale_DefaultsWhenStorageEmpty()
    {
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        // Default master flag ON so a fresh helm gets readable tiles
        // past a chart's native max out of the box (otherwise charts
        // with maxzoom < 18 render as grey with no 404 because Leaflet
        // doesn't fire requests above maxZoom).
        await Assert.That(svc.ChartUpscaleEnabled).IsTrue();
        // Default levels = ChartUpscale.DefaultLevels = 2.
        await Assert.That(svc.ChartUpscaleLevels)
            .IsEqualTo(OnaPlotter.Utilities.ChartUpscale.DefaultLevels);
    }

    [Test]
    public async Task ChartUpscale_RoundTrips()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetChartUpscaleEnabledAsync(true);
        await svc.SetChartUpscaleLevelsAsync(3);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.ChartUpscaleEnabled).IsTrue();
        await Assert.That(svc2.ChartUpscaleLevels).IsEqualTo(3);
    }

    [Test]
    [Arguments(true, 0)]
    [Arguments(true, 1)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    [Arguments(false, 0)]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    public async Task ChartUpscale_RoundTripMatrix(bool enabled, int levels)
    {
        // Pins every (enabled, levels) pair through Set + reload. A
        // hypothetical "if (value == _current) return early" optimisation
        // in the setters would fail on the case where the helm's first
        // explicit toggle matches the default (true, 2) -- write would
        // be skipped, the second instance would read default-true, and
        // a later default flip would silently change the behaviour.
        // Same for the false-side: a helm who explicitly opts out today
        // would silently get re-enabled if the early-return shortcut
        // were ever introduced and the default flipped again.
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetChartUpscaleEnabledAsync(enabled);
        await svc.SetChartUpscaleLevelsAsync(levels);

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.ChartUpscaleEnabled).IsEqualTo(enabled);
        await Assert.That(svc2.ChartUpscaleLevels).IsEqualTo(levels);
    }

    [Test]
    public async Task SetChartUpscaleEnabledAsync_StorageThrows_KeepsInMemoryStateAndDoesNotCrash()
    {
        // Private-mode / quota-exceeded path. The setter must update
        // the in-memory value (so the layer renders correctly within
        // the session) but swallow the persistence failure. Without
        // this, a fresh user toggling the master flag in private mode
        // would freeze the Settings page on a JSException bubble-up.
        var svc = new AppSettingsService(new ThrowingKv());
        await svc.InitializeAsync();

        await svc.SetChartUpscaleEnabledAsync(false);

        await Assert.That(svc.ChartUpscaleEnabled).IsFalse();
    }

    [Test]
    public async Task SetChartUpscaleLevelsAsync_StorageThrows_KeepsInMemoryStateAndDoesNotCrash()
    {
        var svc = new AppSettingsService(new ThrowingKv());
        await svc.InitializeAsync();

        await svc.SetChartUpscaleLevelsAsync(3);

        await Assert.That(svc.ChartUpscaleLevels).IsEqualTo(3);
    }

    [Test]
    public async Task SetChartUpscaleLevels_BelowFloor_ClampsToZero()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetChartUpscaleLevelsAsync(-5);

        await Assert.That(svc.ChartUpscaleLevels).IsEqualTo(0);
        await Assert.That(await kv.GetAsync("chartUpscaleLevels.v1")).IsEqualTo("0");
    }

    [Test]
    public async Task SetChartUpscaleLevels_AboveCeiling_ClampsToThree()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();

        await svc.SetChartUpscaleLevelsAsync(99);

        await Assert.That(svc.ChartUpscaleLevels).IsEqualTo(3);
        await Assert.That(await kv.GetAsync("chartUpscaleLevels.v1")).IsEqualTo("3");
    }

    [Test]
    // -------------------- pinned-by-cast cases --------------------
    // (these would surface if (int)LoadDouble changed truncation rules)
    [Arguments("-5", 0)]            // below floor -> clamp 0
    [Arguments("-1", 0)]
    [Arguments("0", 0)]
    [Arguments("1", 1)]
    [Arguments("2", 2)]
    [Arguments("3", 3)]
    [Arguments("4", 3)]             // above ceiling -> clamp 3
    [Arguments("99", 3)]
    [Arguments("2.7", 2)]           // double truncates toward zero
    [Arguments("-2.7", 0)]          // truncation gives -2 then clamp -> 0
    [Arguments("-0.4", 0)]
    // -------------------- pinned-by-clamp cases --------------------
    // (LoadDouble accepts these; the clamp catches the overflow)
    [Arguments("Infinity", 3)]      // (int)+Inf saturates to int.MaxValue, clamp -> 3
    [Arguments("-Infinity", 0)]     // (int)-Inf saturates to int.MinValue, clamp -> 0
    [Arguments("1e308", 3)]         // double-overflow region
    [Arguments("-1e308", 0)]
    [Arguments("9999999999999", 3)] // long > int.MaxValue, cast then clamp
    [Arguments("-9999999999999", 0)]
    public async Task ChartUpscaleLevels_StoredValue_LoadsClamped(
        string stored, int expected)
    {
        // Feeds literal localStorage values through the InitializeAsync
        // load path. Covers the "(int)await LoadDouble" cast plus the
        // ClampLevels guard. The arguments are split into two visual
        // groups by the comments above: the first group is "pinned by
        // cast semantics" (a refactor that switched to Math.Floor or
        // dropped IsFinite would surface); the second is "pinned by
        // clamp" (LoadDouble currently accepts these but ClampLevels
        // catches the result -- a regression that drops Math.Clamp
        // would surface).
        var kv = new InMemoryKv();
        await kv.SetAsync("chartUpscaleLevels.v1", stored);
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await Assert.That(svc.ChartUpscaleLevels).IsEqualTo(expected);
    }

    [Test]
    [Arguments("NaN")]              // double.TryParse accepts; (int)NaN = 0; clamp -> 0
    public async Task ChartUpscaleLevels_NaNStored_LoadsAsZero(string stored)
    {
        // Carved out from the parametrized test above because NaN's
        // expected value (0 on .NET) is technically "implementation-
        // defined" by the C# spec and worth its own narrative comment.
        // .NET 5+ fixed (int)NaN -> 0 to match ECMA-335; older runtimes
        // could differ. Pin so a runtime upgrade that flipped this
        // would be a visible test change rather than a silent default
        // shift.
        var kv = new InMemoryKv();
        await kv.SetAsync("chartUpscaleLevels.v1", stored);
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await Assert.That(svc.ChartUpscaleLevels).IsEqualTo(0);
    }

    [Test]
    [Arguments("banana")]
    [Arguments("")]
    [Arguments("2,5")]              // comma locale -- LoadDouble rejects
    public async Task ChartUpscaleLevels_GarbageStored_FallsBackToDefault(string stored)
    {
        // Parse failure -> LoadDouble returns its fallback (DefaultLevels),
        // ClampLevels passes it through untouched. The helm sees the
        // recommended "2" rather than 0 (which would silently disable
        // overzoom for someone whose master flag is on).
        var kv = new InMemoryKv();
        await kv.SetAsync("chartUpscaleLevels.v1", stored);
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await Assert.That(svc.ChartUpscaleLevels)
            .IsEqualTo(OnaPlotter.Utilities.ChartUpscale.DefaultLevels);
    }

    [Test]
    public async Task SetChartUpscale_FiresOnSettingsChanged()
    {
        // Both setters are on the OnSettingsChanged hot path so the
        // Map page re-evaluates layer options when the helm flips
        // either knob. Without the event the chart would keep
        // rendering with stale upscale levels until the next chart
        // toggle.
        var svc = new AppSettingsService(new InMemoryKv());
        await svc.InitializeAsync();
        int fires = 0;
        svc.OnSettingsChanged += () => fires++;

        await svc.SetChartUpscaleEnabledAsync(true);
        await svc.SetChartUpscaleLevelsAsync(3);

        await Assert.That(fires).IsEqualTo(2);
    }

    [Test]
    public async Task ChartUpscaleEnabled_ExplicitFalse_StaysFalse()
    {
        // Helms who explicitly opted out (before the default flipped
        // to true) keep their choice on next load. Pin so a future
        // refactor can't silently re-enable overzoom for someone who
        // turned it off.
        var kv = new InMemoryKv();
        await kv.SetAsync("chartUpscaleEnabled.v1", "false");
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await Assert.That(svc.ChartUpscaleEnabled).IsFalse();
    }

    [Test]
    [Arguments("yes please")]   // free-form garbage
    [Arguments("True")]         // wrong case
    [Arguments("TRUE")]         // shouting case
    [Arguments("1")]            // C-style truthy
    [Arguments("")]             // empty string (distinct from missing key)
    [Arguments(" true")]        // leading whitespace
    [Arguments("true ")]        // trailing whitespace
    [Arguments("yes")]          // i18n / aliasing attempt
    public async Task ChartUpscaleEnabled_NonExactTrueStored_LoadsAsFalse(string stored)
    {
        // LoadBool returns false for any stored value that isn't the
        // exact literal "true" (and only falls back to the default
        // when the key is absent). The asymmetry matters under the
        // PR #160 default-on flip: corruption-path helms land in
        // upscale-off rather than the new default. Pinned across the
        // boundary cases a future "loosen LoadBool" change would
        // most plausibly hit -- a switch to OrdinalIgnoreCase, a
        // .Trim(), or bool.TryParse -- so the asymmetry is visible
        // in tests rather than only in the docstring on
        // ChartUpscaleEnabled.
        var kv = new InMemoryKv();
        await kv.SetAsync("chartUpscaleEnabled.v1", stored);
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await Assert.That(svc.ChartUpscaleEnabled).IsFalse();
    }

    [Test]
    public async Task MarkChartsSeededAsync_PersistsAndReflects()
    {
        var kv = new InMemoryKv();
        var svc = new AppSettingsService(kv);
        await svc.InitializeAsync();
        await Assert.That(svc.ChartsSeeded).IsFalse();

        await svc.MarkChartsSeededAsync();
        await Assert.That(svc.ChartsSeeded).IsTrue();

        var svc2 = new AppSettingsService(kv);
        await svc2.InitializeAsync();
        await Assert.That(svc2.ChartsSeeded).IsTrue();
    }

    private sealed class ThrowingKv : IKeyValueStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => throw new Microsoft.JSInterop.JSException("storage disabled (test)");
        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => throw new Microsoft.JSInterop.JSException("storage disabled (test)");
        public Task RemoveAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
