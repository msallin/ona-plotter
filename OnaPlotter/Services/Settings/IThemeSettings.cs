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
    /// <summary>Red-shift overlay applied on top of the base theme.</summary>
    bool NightMode { get; }

    /// <summary>When true, Night mode auto-engages based on
    /// SignalK's <c>environment.sun</c> string path. A manual
    /// toggle suppresses the auto-flip for 12 h via
    /// <see cref="LastManualNightToggleUtc"/>.</summary>
    bool NightModeAuto { get; }

    /// <summary>Night-mode flavour: "soft" / "amber" / "red".</summary>
    string NightModePreset { get; }

    /// <summary>UTC timestamp of the most recent manual Night toggle.
    /// Persisted so the 12-hour auto-suppress window survives reload.</summary>
    DateTime? LastManualNightToggleUtc { get; }

    /// <summary>"system" (follow OS) / "light" / "dark". Independent
    /// of <see cref="NightMode"/> -- night applies on top.</summary>
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
    Task SetNightModePresetAsync(string value);
    Task MarkManualNightToggleAsync();
    Task SetThemeAsync(string value);
    Task SetSidebarCollapsedAsync(bool value);
    Task SetShowKeyboardHintsAsync(bool value);
    Task SetKeepScreenAwakeAsync(bool value);

    /// <summary>Apply a first-run sidebar default that depends on
    /// the caller-supplied viewport hint. No-op once the user has
    /// explicitly toggled the chevron.</summary>
    Task ApplyMobileFirstRunDefaultsAsync(bool isMobile);
}
