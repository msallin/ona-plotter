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
}
