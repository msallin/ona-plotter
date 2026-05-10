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

    /// <summary>Back-compat boolean: derived from the True/Magnetic
    /// axis of <see cref="CogReadoutSource"/>. True when the helm's
    /// HUD-readout pick is a Magnetic variant. Existing read sites
    /// (NavigationData.CourseOverGround, AlarmContext) keep using
    /// this; the new four-way picker writes through to
    /// <see cref="CogReadoutSource"/> and this property derives.</summary>
    bool PreferMagneticCourse { get; }

    /// <summary>Helm-picked source for the HUD numerical COG
    /// readout. Persisted as <c>cogReadoutSource.v1</c>; persisted
    /// strings are <c>"trueSmoothed"</c> / <c>"trueRealtime"</c> /
    /// <c>"magneticSmoothed"</c> / <c>"magneticRealtime"</c>. Default
    /// <c>"trueSmoothed"</c> matches the helm-friendly steady number
    /// the HUD has shown for a while. See
    /// <see cref="OnaPlotter.Utilities.CogSourceResolver"/>.</summary>
    string CogReadoutSource { get; }

    /// <summary>Helm-picked source for the on-map own-COG vector
    /// (the line extending forward from the boat). Persisted as
    /// <c>ownCogVectorSource.v1</c>; same persisted strings as
    /// <see cref="CogReadoutSource"/>. Default
    /// <c>"trueSmoothed"</c>; helms doing close-quarter manoeuvres
    /// can switch to a realtime variant for immediate steering
    /// feedback while keeping the readout smoothed.</summary>
    string OwnCogVectorSource { get; }

    /// <summary>When true, the client auto-advances to the next
    /// waypoint on perpendicularPassed / arrivalCircleEntered.</summary>
    bool AutoAdvanceWaypoints { get; }

    /// <summary>Helm-configured arrival-circle radius (metres) sent to
    /// the SignalK v2 Course API on every Set-Destination /
    /// Set-Active-Route request. The server adopts it as the
    /// effective <c>navigation.course.arrivalCircle</c> for the
    /// active course; downstream consumers (HUD ring, APPROACH alarm,
    /// auto-advance) keep reading the SK path so a peer plotter
    /// changing the circle mid-passage propagates here too. The
    /// local setting only seeds new courses started from this
    /// plotter.</summary>
    double ArrivalCircleMeters { get; }

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
    Task SetArrivalCircleMetersAsync(double value);
    Task SetCogReadoutSourceAsync(string value);
    Task SetOwnCogVectorSourceAsync(string value);
}
