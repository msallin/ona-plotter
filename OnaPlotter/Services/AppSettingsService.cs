using System.Globalization;

namespace OnaPlotter.Services;

/// <summary>
/// Singleton user preferences store. Reads initial values from
/// <see cref="IKeyValueStore"/> on first access and writes back on change.
/// <para>
/// Keys that cannot be read (storage disabled, private browsing) fall back
/// to defaults without surfacing the error - reading a missing preference
/// is not an error condition.
/// </para>
/// </summary>
public sealed class AppSettingsService : IAppSettings
{
    private readonly IKeyValueStore _store;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public bool NightMode { get; private set; }
    public bool NightModeAuto { get; private set; } = false;
    public string NightModePreset { get; private set; } = "soft";
    public string Theme { get; private set; } = "system";
    public string MapOrientation { get; private set; } = "north";
    public bool FollowBoat { get; private set; } = true;
    public bool LaylinesVisible { get; private set; }
    public double DepthAlarmThreshold { get; private set; } = 3.0;
    public double CpaAlarmThreshold { get; private set; } = 0.5;
    public double GuardZoneLookaheadMinutes { get; private set; } = 10.0;
    public double GuardZoneWarningFactor { get; private set; } = 2.0;
    public double WindShiftAlarmThreshold { get; private set; } = 15.0;
    public double WindShiftLookbackMinutes { get; private set; } = 5.0;
    public double BoatDraftMeters { get; private set; } = 1.5;
    public double AnchorTideSafetyMargin { get; private set; } = 1.0;
    public double ManualAnchorRadiusMeters { get; private set; } = 30.0;
    public double DeadmanTimeoutMinutes { get; private set; } = 0.0;
    public double DeadmanNightMinutes { get; private set; } = 15.0;
    public int SnoozeDurationMinutes { get; private set; } = 10;
    public bool BigType { get; private set; } = false;
    public string SailingMode { get; private set; } = "cruise";
    public bool KeepScreenAwake { get; private set; } = true;
    public double WaypointArrivalRadiusMeters { get; private set; } = 50.0;
    public bool ShowKeyboardHints { get; private set; } = false;
    public bool ShowAutopilotHud { get; private set; } = false;

    private readonly HashSet<string> _enabledChartIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enabledRouteIds = new(StringComparer.Ordinal);
    private readonly List<string> _chartOrder = [];
    public IReadOnlySet<string> EnabledChartIds => _enabledChartIds;
    public IReadOnlySet<string> EnabledRouteIds => _enabledRouteIds;
    public IReadOnlyList<string> ChartOrder => _chartOrder;

    public event Action? OnSettingsChanged;

    public AppSettingsService(IKeyValueStore store) => _store = store;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            NightMode = await LoadBool("nightMode", false);
            NightModeAuto = await LoadBool("nightModeAuto.v1", false);
            NightModePreset = NormalizeNightPreset(await LoadString("nightModePreset"));
            Theme = NormalizeTheme(await LoadString("theme"));
            MapOrientation = await LoadString("mapOrientation") ?? "north";
            FollowBoat = await LoadBool("followBoat", true);
            LaylinesVisible = await LoadBool("laylinesVisible", false);
            DepthAlarmThreshold = await LoadDouble("depthAlarmThreshold", 3.0);
            CpaAlarmThreshold = await LoadDouble("cpaAlarmThreshold", 0.5);
            GuardZoneLookaheadMinutes = await LoadDouble("guardZoneLookaheadMinutes", 10.0);
            GuardZoneWarningFactor = await LoadDouble("guardZoneWarningFactor", 2.0);
            WindShiftAlarmThreshold = await LoadDouble("windShiftAlarmThreshold", 15.0);
            WindShiftLookbackMinutes = await LoadDouble("windShiftLookbackMinutes", 5.0);
            BoatDraftMeters = await LoadDouble("boatDraftMeters", 1.5);
            AnchorTideSafetyMargin = await LoadDouble("anchorTideSafetyMargin", 1.0);
            ManualAnchorRadiusMeters = await LoadDouble("manualAnchorRadiusMeters.v1", 30.0);
            DeadmanTimeoutMinutes = await LoadDouble("deadmanTimeoutMinutes.v1", 0.0);
            DeadmanNightMinutes = await LoadDouble("deadmanNightMinutes.v1", 15.0);
            SnoozeDurationMinutes = (int)await LoadDouble("snoozeDurationMinutes.v1", 10.0);
            BigType = await LoadBool("bigType.v1", false);
            SailingMode = NormalizeSailingMode(await LoadString("sailingMode"));
            KeepScreenAwake = await LoadBool("keepScreenAwake.v1", true);
            WaypointArrivalRadiusMeters = await LoadDouble("waypointArrivalRadiusMeters.v1", 50.0);
            ShowKeyboardHints = await LoadBool("showKeyboardHints.v1", false);
            ShowAutopilotHud = await LoadBool("showAutopilotHud.v1", false);
            LoadIdsInto(await LoadString("enabledChartIds"), _enabledChartIds);
            LoadIdsInto(await LoadString("enabledRouteIds"), _enabledRouteIds);
            LoadIdsInto(await LoadString("chartOrder.v1"), _chartOrder);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task SetNightModeAsync(bool value)
    {
        NightMode = value;
        await Save("nightMode", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetNightModeAutoAsync(bool value)
    {
        NightModeAuto = value;
        await Save("nightModeAuto.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetNightModePresetAsync(string value)
    {
        NightModePreset = NormalizeNightPreset(value);
        await Save("nightModePreset", NightModePreset);
        OnSettingsChanged?.Invoke();
    }

    public async Task SetThemeAsync(string value)
    {
        Theme = NormalizeTheme(value);
        await Save("theme", Theme);
        OnSettingsChanged?.Invoke();
    }

    private static string NormalizeTheme(string? raw) => raw switch
    {
        "light" or "dark" or "system" or "high-contrast" => raw,
        _ => "system",
    };

    private static string NormalizeNightPreset(string? raw) => raw switch
    {
        "soft" or "amber" or "red" => raw,
        _ => "soft",
    };

    public async Task SetMapOrientationAsync(string value)
    {
        MapOrientation = value;
        await Save("mapOrientation", value);
        OnSettingsChanged?.Invoke();
    }

    public async Task SetFollowBoatAsync(bool value)
    {
        FollowBoat = value;
        await Save("followBoat", value ? "true" : "false");
    }

    public async Task SetLaylinesVisibleAsync(bool value)
    {
        LaylinesVisible = value;
        await Save("laylinesVisible", value ? "true" : "false");
    }

    public async Task SetDepthAlarmThresholdAsync(double value)
    {
        DepthAlarmThreshold = value;
        await Save("depthAlarmThreshold", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetCpaAlarmThresholdAsync(double value)
    {
        CpaAlarmThreshold = value;
        await Save("cpaAlarmThreshold", value.ToString("F2", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetGuardZoneLookaheadMinutesAsync(double value)
    {
        GuardZoneLookaheadMinutes = value;
        await Save("guardZoneLookaheadMinutes", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetGuardZoneWarningFactorAsync(double value)
    {
        GuardZoneWarningFactor = value;
        await Save("guardZoneWarningFactor", value.ToString("F2", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetWindShiftAlarmThresholdAsync(double value)
    {
        WindShiftAlarmThreshold = value;
        await Save("windShiftAlarmThreshold", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetWindShiftLookbackMinutesAsync(double value)
    {
        WindShiftLookbackMinutes = value;
        await Save("windShiftLookbackMinutes", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetBoatDraftMetersAsync(double value)
    {
        BoatDraftMeters = value;
        await Save("boatDraftMeters", value.ToString("F2", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetAnchorTideSafetyMarginAsync(double value)
    {
        AnchorTideSafetyMargin = value;
        await Save("anchorTideSafetyMargin", value.ToString("F2", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetSailingModeAsync(string value)
    {
        SailingMode = NormalizeSailingMode(value);
        await Save("sailingMode", SailingMode);
        OnSettingsChanged?.Invoke();
    }

    public async Task SetKeepScreenAwakeAsync(bool value)
    {
        KeepScreenAwake = value;
        await Save("keepScreenAwake.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetWaypointArrivalRadiusMetersAsync(double value)
    {
        WaypointArrivalRadiusMeters = value;
        await Save("waypointArrivalRadiusMeters.v1", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetShowKeyboardHintsAsync(bool value)
    {
        ShowKeyboardHints = value;
        await Save("showKeyboardHints.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetShowAutopilotHudAsync(bool value)
    {
        ShowAutopilotHud = value;
        await Save("showAutopilotHud.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetManualAnchorRadiusMetersAsync(double value)
    {
        ManualAnchorRadiusMeters = value;
        await Save("manualAnchorRadiusMeters.v1", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetDeadmanTimeoutMinutesAsync(double value)
    {
        DeadmanTimeoutMinutes = value;
        await Save("deadmanTimeoutMinutes.v1", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetDeadmanNightMinutesAsync(double value)
    {
        DeadmanNightMinutes = value;
        await Save("deadmanNightMinutes.v1", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetSnoozeDurationMinutesAsync(int value)
    {
        // Clamp 1 min minimum (zero would snooze forever -- that's dismiss).
        // Upper bound 120 to keep a runaway value from locking an alarm
        // quiet for days after a power cycle.
        SnoozeDurationMinutes = System.Math.Clamp(value, 1, 120);
        await Save("snoozeDurationMinutes.v1", SnoozeDurationMinutes.ToString(CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetBigTypeAsync(bool value)
    {
        BigType = value;
        await Save("bigType.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    private static string NormalizeSailingMode(string? raw) => raw switch
    {
        "cruise" or "race" => raw,
        _ => "cruise",
    };

    public async Task SetEnabledChartsAsync(IEnumerable<string> ids)
    {
        // Materialise first -- see SetChartOrderAsync for the aliasing
        // footgun this defends against.
        var copy = ids.ToList();
        _enabledChartIds.Clear();
        foreach (var id in copy) _enabledChartIds.Add(id);
        await Save("enabledChartIds", string.Join('\n', _enabledChartIds));
    }

    public async Task SetEnabledRoutesAsync(IEnumerable<string> ids)
    {
        var copy = ids.ToList();
        _enabledRouteIds.Clear();
        foreach (var id in copy) _enabledRouteIds.Add(id);
        await Save("enabledRouteIds", string.Join('\n', _enabledRouteIds));
    }

    public async Task SetChartOrderAsync(IEnumerable<string> ids)
    {
        // Materialise BEFORE clearing _chartOrder -- otherwise a caller
        // passing `Settings.ChartOrder.Append(x)` (a LINQ enumerable
        // referencing _chartOrder) iterates an empty list and loses
        // every previously-ordered chart. This was the root cause of
        // the "chart reorder buttons do nothing" bug: each Toggle wiped
        // the order to just the newly-toggled chart, leaving subsequent
        // reorders with missing neighbours that failed the bounds check
        // in ReorderChart.
        var copy = ids.ToList();
        _chartOrder.Clear();
        foreach (var id in copy)
        {
            if (!string.IsNullOrEmpty(id) && !_chartOrder.Contains(id)) _chartOrder.Add(id);
        }
        await Save("chartOrder.v1", string.Join('\n', _chartOrder));
        OnSettingsChanged?.Invoke();
    }

    // --- storage helpers: swallow read errors (missing key = default), propagate write errors ---

    private async Task Save(string key, string value)
    {
        try { await _store.SetAsync(key, value); }
        catch (Microsoft.JSInterop.JSException) { /* storage disabled - user sees nothing persisted; acceptable. */ }
    }

    private async Task<string?> LoadString(string key)
    {
        try { return await _store.GetAsync(key); }
        catch (Microsoft.JSInterop.JSException) { return null; }
    }

    private async Task<bool> LoadBool(string key, bool fallback)
    {
        var v = await LoadString(key);
        return v is not null ? v == "true" : fallback;
    }

    private async Task<double> LoadDouble(string key, double fallback)
    {
        var v = await LoadString(key);
        return v is not null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d : fallback;
    }

    private static void LoadIdsInto(string? raw, HashSet<string> target)
    {
        target.Clear();
        if (string.IsNullOrEmpty(raw)) return;
        foreach (var id in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            target.Add(id);
    }

    // Ordered variant: preserves list order (unlike HashSet) so
    // `chartOrder.v1` round-trips the user's chosen draw order exactly.
    private static void LoadIdsInto(string? raw, List<string> target)
    {
        target.Clear();
        if (string.IsNullOrEmpty(raw)) return;
        foreach (var id in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            target.Add(id);
    }
}
