using OnaPlotter.Models;

namespace OnaPlotter.Services.Signalk;

/// <summary>
/// Mirrors the helm's true-vs-magnetic heading / COG preference from
/// settings onto <see cref="NavigationData"/>, which resolves
/// <c>Heading</c> / <c>CourseOverGround</c> from the cached raw values,
/// and nudges listeners when the preference flips so HUDs re-derive
/// without waiting for the next delta.
/// </summary>
internal sealed class HeadingPreferenceSync : IDisposable
{
    private readonly IAppSettings _settings;
    private readonly NavigationData _data;
    private readonly Action _onChanged;

    // OnSettingsChanged fires for ANY setting (font size, harbor mode,
    // night mode...). Comparing against a snapshot of the two flags
    // used here keeps unrelated tweaks from cascading through every
    // HUD + alarm rule.
    private bool _cachedPreferMagneticHeading;
    private bool _cachedPreferMagneticCourse;

    /// <summary>Re-entrancy guard for OnSettingsChanged -> onChanged ->
    /// (listener) -> settings write -> OnSettingsChanged. The snapshot
    /// converges in one re-entrant hop, so without the guard a single
    /// flip would notify twice.</summary>
    private bool _inOnSettingsChanged;

    public HeadingPreferenceSync(IAppSettings settings, NavigationData data, Action onChanged)
    {
        _settings = settings;
        _data = data;
        _onChanged = onChanged;
        Apply();
        _cachedPreferMagneticHeading = settings.PreferMagneticHeading;
        _cachedPreferMagneticCourse = settings.PreferMagneticCourse;
        settings.OnSettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        if (_inOnSettingsChanged)
        {
            // Re-entered: refresh the snapshot so the outer pass sees the
            // current settings, then bail.
            _cachedPreferMagneticHeading = _settings.PreferMagneticHeading;
            _cachedPreferMagneticCourse = _settings.PreferMagneticCourse;
            return;
        }
        _inOnSettingsChanged = true;
        try
        {
            bool magHeading = _settings.PreferMagneticHeading;
            bool magCourse = _settings.PreferMagneticCourse;
            if (magHeading == _cachedPreferMagneticHeading
                && magCourse == _cachedPreferMagneticCourse)
            {
                return;
            }
            _cachedPreferMagneticHeading = magHeading;
            _cachedPreferMagneticCourse = magCourse;
            Apply();
            _onChanged();
        }
        finally
        {
            _inOnSettingsChanged = false;
        }
    }

    private void Apply()
    {
        _data.PreferMagneticHeading = _settings.PreferMagneticHeading;
        _data.PreferMagneticCourse = _settings.PreferMagneticCourse;
    }

    public void Dispose() => _settings.OnSettingsChanged -= OnSettingsChanged;
}
