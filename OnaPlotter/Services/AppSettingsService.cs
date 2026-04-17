// Shared application state that persists across page navigation.
// Night mode, map preferences, and alarm thresholds are stored here
// and optionally persisted to localStorage via JS interop.

using Microsoft.JSInterop;

namespace OnaPlotter.Services;

/// <summary>
/// Application-wide settings. Registered as singleton so all pages share state.
/// </summary>
public sealed class AppSettingsService
{
    private readonly IJSRuntime _js;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public bool NightMode { get; private set; }
    public string MapOrientation { get; private set; } = "north";
    public bool FollowBoat { get; private set; } = true;
    public bool LaylinesVisible { get; private set; }

    // Alarm thresholds
    public double DepthAlarmThreshold { get; private set; } = 3.0;  // meters
    public double CpaAlarmThreshold { get; private set; } = 0.5;    // nautical miles
    public double WindShiftAlarmThreshold { get; private set; } = 15.0; // degrees

    public event Action? OnSettingsChanged;

    public AppSettingsService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return; // Double-check after acquiring lock.

            try
            {
                NightMode = await LoadBoolAsync("nightMode", false);
                MapOrientation = await LoadStringAsync("mapOrientation", "north") ?? "north";
                FollowBoat = await LoadBoolAsync("followBoat", true);
                LaylinesVisible = await LoadBoolAsync("laylinesVisible", false);
                DepthAlarmThreshold = await LoadDoubleAsync("depthAlarmThreshold", 3.0);
                CpaAlarmThreshold = await LoadDoubleAsync("cpaAlarmThreshold", 0.5);
                WindShiftAlarmThreshold = await LoadDoubleAsync("windShiftAlarmThreshold", 15.0);
            }
            catch
            {
                // localStorage might not be available (private browsing, etc.).
            }

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
        await SaveAsync("nightMode", value.ToString().ToLowerInvariant());
        OnSettingsChanged?.Invoke();
    }

    public async Task SetMapOrientationAsync(string value)
    {
        MapOrientation = value;
        await SaveAsync("mapOrientation", value);
        OnSettingsChanged?.Invoke();
    }

    public async Task SetFollowBoatAsync(bool value)
    {
        FollowBoat = value;
        await SaveAsync("followBoat", value.ToString().ToLowerInvariant());
    }

    public async Task SetLaylinesVisibleAsync(bool value)
    {
        LaylinesVisible = value;
        await SaveAsync("laylinesVisible", value.ToString().ToLowerInvariant());
    }

    public async Task SetDepthAlarmThresholdAsync(double value)
    {
        DepthAlarmThreshold = value;
        await SaveAsync("depthAlarmThreshold", value.ToString("F1"));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetCpaAlarmThresholdAsync(double value)
    {
        CpaAlarmThreshold = value;
        await SaveAsync("cpaAlarmThreshold", value.ToString("F2"));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetWindShiftAlarmThresholdAsync(double value)
    {
        WindShiftAlarmThreshold = value;
        await SaveAsync("windShiftAlarmThreshold", value.ToString("F1"));
        OnSettingsChanged?.Invoke();
    }

    private async Task SaveAsync(string key, string value)
    {
        try { await _js.InvokeVoidAsync("localStorage.setItem", $"ona.{key}", value); }
        catch { /* Ignore storage errors. */ }
    }

    private async Task<bool> LoadBoolAsync(string key, bool fallback)
    {
        var val = await LoadStringAsync(key, null);
        return val is not null ? val == "true" : fallback;
    }

    private async Task<double> LoadDoubleAsync(string key, double fallback)
    {
        var val = await LoadStringAsync(key, null);
        return val is not null && double.TryParse(val, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : fallback;
    }

    private async Task<string?> LoadStringAsync(string key, string? fallback)
    {
        try
        {
            var val = await _js.InvokeAsync<string?>("localStorage.getItem", $"ona.{key}");
            return val ?? fallback;
        }
        catch { return fallback; }
    }
}
