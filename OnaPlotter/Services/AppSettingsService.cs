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

    private readonly HashSet<string> _enabledChartIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enabledRouteIds = new(StringComparer.Ordinal);
    public IReadOnlySet<string> EnabledChartIds => _enabledChartIds;
    public IReadOnlySet<string> EnabledRouteIds => _enabledRouteIds;

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
            LoadIdsInto(await LoadString("enabledChartIds"), _enabledChartIds);
            LoadIdsInto(await LoadString("enabledRouteIds"), _enabledRouteIds);
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

    public async Task SetThemeAsync(string value)
    {
        Theme = NormalizeTheme(value);
        await Save("theme", Theme);
        OnSettingsChanged?.Invoke();
    }

    private static string NormalizeTheme(string? raw) => raw switch
    {
        "light" or "dark" or "system" => raw,
        _ => "system",
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

    public async Task SetEnabledChartsAsync(IEnumerable<string> ids)
    {
        _enabledChartIds.Clear();
        foreach (var id in ids) _enabledChartIds.Add(id);
        await Save("enabledChartIds", string.Join('\n', _enabledChartIds));
    }

    public async Task SetEnabledRoutesAsync(IEnumerable<string> ids)
    {
        _enabledRouteIds.Clear();
        foreach (var id in ids) _enabledRouteIds.Add(id);
        await Save("enabledRouteIds", string.Join('\n', _enabledRouteIds));
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
}
