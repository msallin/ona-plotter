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
    public string MapOrientation { get; private set; } = "north";
    public bool FollowBoat { get; private set; } = true;
    public bool LaylinesVisible { get; private set; }
    public double DepthAlarmThreshold { get; private set; } = 3.0;
    public double CpaAlarmThreshold { get; private set; } = 0.5;
    public double WindShiftAlarmThreshold { get; private set; } = 15.0;

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
            MapOrientation = await LoadString("mapOrientation") ?? "north";
            FollowBoat = await LoadBool("followBoat", true);
            LaylinesVisible = await LoadBool("laylinesVisible", false);
            DepthAlarmThreshold = await LoadDouble("depthAlarmThreshold", 3.0);
            CpaAlarmThreshold = await LoadDouble("cpaAlarmThreshold", 0.5);
            WindShiftAlarmThreshold = await LoadDouble("windShiftAlarmThreshold", 15.0);
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

    public async Task SetWindShiftAlarmThresholdAsync(double value)
    {
        WindShiftAlarmThreshold = value;
        await Save("windShiftAlarmThreshold", value.ToString("F1", CultureInfo.InvariantCulture));
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
}
