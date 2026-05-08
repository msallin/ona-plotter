namespace OnaPlotter.Services.Settings;

/// <summary>
/// Narrow surface for theme + chrome consumers (NavMenu sidebar,
/// MainLayout, Settings page theme dialog). Includes the night-mode
/// toggle since it's effectively a theme overlay.
///
/// <para>Carved from <see cref="IAppSettings"/> as part of ARCH-003.
/// </para>
/// </summary>
public interface IThemeSettings
{
    /// <summary>Red-shift overlay applied on top of the base theme.
    /// One binary toggle - the previous four-step cycle (dusk / soft
    /// / amber / red) was helm-flagged as confusing; helms wanting a
    /// "dark intermediate without red shift" can use
    /// <see cref="Theme"/> = "dark" instead.</summary>
    bool NightMode { get; }

    /// <summary>When true, Night mode auto-engages based on
    /// SignalK's <c>environment.sun</c> string path. A manual
    /// toggle suppresses the auto-flip until the next day/night
    /// transition via <see cref="LastManualNightOverrideSunCluster"/>;
    /// <see cref="LastManualNightToggleUtc"/> is kept as a redundancy
    /// timestamp.</summary>
    bool NightModeAuto { get; }

    /// <summary>UTC timestamp of the most recent manual Night toggle.
    /// Persisted so the auto-suppress window survives reload.</summary>
    DateTime? LastManualNightToggleUtc { get; }

    /// <summary>The <c>environment.sun</c> cluster ("day" / "night")
    /// at the moment of the last manual Night toggle. Auto-night
    /// suppresses while the current cluster matches this value; the
    /// next cluster transition (sunrise / sunset) clears the override
    /// so auto-night resumes. Null when no override is active OR
    /// when env.sun was unknown at toggle time. Replaces the
    /// previous fixed 12-hour window which fought helms who wanted
    /// their override to last the whole watch.</summary>
    string? LastManualNightOverrideSunCluster { get; }

    /// <summary>"system" (follow OS) / "light" / "dark". Independent
    /// of <see cref="NightMode"/> - night applies on top.</summary>
    string Theme { get; }

    /// <summary>When true, the sidebar collapses to icon-only.</summary>
    bool SidebarCollapsed { get; }

    /// <summary>When true, keyboard-shortcut hints are visible
    /// inline + the "?" shortcut dialog is enabled.</summary>
    bool ShowKeyboardHints { get; }

    /// <summary>When true, hold a Screen Wake Lock while the app is
    /// in the foreground.</summary>
    bool KeepScreenAwake { get; }

    Task SetNightModeAsync(bool value);
    Task SetNightModeAutoAsync(bool value);
    /// <summary>Record a manual Night-mode toggle. <paramref name="sunCluster"/>
    /// should be "day" or "night" (the cluster of the current
    /// <c>environment.sun</c> value at toggle time), or null when
    /// the path is unknown. Cluster-suppression is the primary
    /// auto-night-override mechanism; the timestamp is the
    /// redundancy backstop.</summary>
    Task MarkManualNightToggleAsync(string? sunCluster);

    /// <summary>Drop the manual-override cluster - called when the
    /// auto-night check sees env.sun transition out of the override
    /// cluster, signalling the helm's manual choice is now stale
    /// and auto-night should resume.</summary>
    Task ClearManualNightOverrideAsync();
    Task SetThemeAsync(string value);
    Task SetSidebarCollapsedAsync(bool value);
    Task SetShowKeyboardHintsAsync(bool value);
    Task SetKeepScreenAwakeAsync(bool value);

    /// <summary>Apply a first-run sidebar default that depends on
    /// the caller-supplied viewport hint. No-op once the user has
    /// explicitly toggled the chevron.</summary>
    Task ApplyMobileFirstRunDefaultsAsync(bool isMobile);
}
