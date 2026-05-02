namespace OnaPlotter.Services.Settings;

/// <summary>
/// Narrow surface for nav-preference consumers (HUD heading
/// resolver, course-advance logic, sailing-mode discriminator).
///
/// <para>Carved from <see cref="IAppSettings"/> as part of ARCH-003.
/// </para>
/// </summary>
public interface INavPreferences
{
    /// <summary>When true, Heading on the HUD / SailSteer / alarms
    /// resolves to <c>navigation.headingMagnetic</c>; otherwise
    /// <c>navigation.headingTrue</c>.</summary>
    bool PreferMagneticHeading { get; }

    /// <summary>When true, COG resolves to
    /// <c>navigation.courseOverGroundMagnetic</c>; otherwise the
    /// true variant.</summary>
    bool PreferMagneticCourse { get; }

    /// <summary>When true, the client auto-advances to the next
    /// waypoint on perpendicularPassed / arrivalCircleEntered.</summary>
    bool AutoAdvanceWaypoints { get; }

    /// <summary>"cruise" (default) or "race". Discriminator for
    /// race-specific overlays (laylines, target speed) so the helm
    /// doesn't toggle a mode flag on every switch.</summary>
    string SailingMode { get; }

    /// <summary>Own vessel propulsion category for COLREGS Rule 18
    /// priority. "power" (default) or "sail". When the helm sets
    /// this to "sail" and an AIS target is power-driven, the CPA
    /// banner / popup classifies the encounter as
    /// power-gives-way-to-sail; when set to "power" and the target
    /// is sailing, we are the give-way vessel. Same-category pairs
    /// fall back to the Rule 13-15 geometry classifier.</summary>
    string OwnVesselType { get; }

    Task SetPreferMagneticHeadingAsync(bool value);
    Task SetPreferMagneticCourseAsync(bool value);
    Task SetAutoAdvanceWaypointsAsync(bool value);
    Task SetSailingModeAsync(string value);
    Task SetOwnVesselTypeAsync(string value);
}
