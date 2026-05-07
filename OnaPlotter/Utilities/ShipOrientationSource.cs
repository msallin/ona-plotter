using OnaPlotter.Models;

namespace OnaPlotter.Utilities;

/// <summary>
/// Helm-picked source for the chart boat-icon rotation. Distinct
/// from the <c>PreferMagneticHeading</c> / <c>PreferMagneticCourse</c>
/// switches which decide which variant the HDG / COG numeric
/// readouts pick: this setting picks the FIELD that drives the
/// graphical boat-icon arrow.
///
/// <para>Default <see cref="HeadingTrue"/>: marine charts are
/// north-true and digital compasses with a true-heading output are
/// the most accurate live source. Boats without a heading sensor
/// fall back through the chain in <see cref="ShipOrientationResolver.Resolve"/>.</para>
/// </summary>
public enum ShipOrientationSource
{
    HeadingTrue,
    HeadingMagnetic,
    CogTrue,
    CogMagnetic,
}

/// <summary>
/// Stringly-typed setting <-> enum + a resolver that picks the
/// orientation angle from <see cref="NavigationData"/> with a
/// deterministic fallback chain.
///
/// <para>The fallback chain prefers the helm's pick first, then
/// the same KIND with the other variant (true vs magnetic), then
/// the other kind in the helm-picked variant, then the last
/// remaining field. Boats that publish only one field still get a
/// rotating boat icon; the chip stays glanceable instead of
/// vanishing because the helm picked a field this server doesn't
/// publish.</para>
/// </summary>
public static class ShipOrientationResolver
{
    /// <summary>Persisted-string default (<c>"headingTrue"</c>).
    /// Mirrored on <see cref="OnaPlotter.Services.Settings.IMapDisplaySettings.ShipOrientationSource"/>'s
    /// initial value.</summary>
    public const string DefaultSetting = "headingTrue";

    /// <summary>Persisted-string &lt;-&gt; enum mapping. Unknown
    /// strings fall back to <see cref="ShipOrientationSource.HeadingTrue"/>
    /// rather than throw - a corrupt localStorage value should
    /// never crash the chart.</summary>
    public static ShipOrientationSource Parse(string? raw) =>
        (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "headingmagnetic" or "headingmag" or "hdgmag" => ShipOrientationSource.HeadingMagnetic,
            "cogtrue" => ShipOrientationSource.CogTrue,
            "cogmagnetic" or "cogmag" => ShipOrientationSource.CogMagnetic,
            _ => ShipOrientationSource.HeadingTrue,
        };

    /// <summary>Round-trip the enum to its persisted-string form.</summary>
    public static string ToSetting(ShipOrientationSource s) => s switch
    {
        ShipOrientationSource.HeadingMagnetic => "headingMagnetic",
        ShipOrientationSource.CogTrue => "cogTrue",
        ShipOrientationSource.CogMagnetic => "cogMagnetic",
        _ => "headingTrue",
    };

    /// <summary>
    /// Resolve the orientation angle (radians) using the helm's
    /// preferred source, with a deterministic fallback through the
    /// remaining three fields. The optional <paramref name="smoothedCog"/>
    /// override is used in place of <c>data.CourseOverGround*</c>
    /// when the resolved field is a COG variant - the boat icon
    /// stops twitching with every wave because the rotation
    /// pulls from <c>NavigationAverages.CogMean30Sec</c>.
    /// </summary>
    /// <returns>The resolved angle in radians, or null when none
    /// of the four fields are published.</returns>
    public static double? Resolve(
        ShipOrientationSource source,
        NavigationData data,
        double? smoothedCog = null)
    {
        // Picked-source first; if null, fall through the chain.
        double? Pick(ShipOrientationSource s) => s switch
        {
            ShipOrientationSource.HeadingTrue => data.HeadingTrue,
            ShipOrientationSource.HeadingMagnetic => data.HeadingMagnetic,
            // COG variants: prefer the smoothed value when the
            // PreferMagneticCourse-resolved smoothed buffer matches
            // the helm's picked variant; otherwise fall back to the
            // raw published variant.
            ShipOrientationSource.CogTrue => smoothedCog ?? data.CourseOverGroundTrue,
            ShipOrientationSource.CogMagnetic => smoothedCog ?? data.CourseOverGroundMagnetic,
            _ => null,
        };

        foreach (var step in FallbackChain(source))
        {
            if (Pick(step) is double v) return v;
        }
        return null;
    }

    /// <summary>Pick order: helm choice -> other variant of same
    /// kind -> other kind in the picked variant -> last field.
    /// Encodes the helm's preference (kind > variant > kind +
    /// variant) without burying it in if/else.</summary>
    private static IEnumerable<ShipOrientationSource> FallbackChain(ShipOrientationSource pick) =>
        pick switch
        {
            ShipOrientationSource.HeadingTrue =>
            [
                ShipOrientationSource.HeadingTrue,
                ShipOrientationSource.HeadingMagnetic,
                ShipOrientationSource.CogTrue,
                ShipOrientationSource.CogMagnetic,
            ],
            ShipOrientationSource.HeadingMagnetic =>
            [
                ShipOrientationSource.HeadingMagnetic,
                ShipOrientationSource.HeadingTrue,
                ShipOrientationSource.CogMagnetic,
                ShipOrientationSource.CogTrue,
            ],
            ShipOrientationSource.CogTrue =>
            [
                ShipOrientationSource.CogTrue,
                ShipOrientationSource.CogMagnetic,
                ShipOrientationSource.HeadingTrue,
                ShipOrientationSource.HeadingMagnetic,
            ],
            ShipOrientationSource.CogMagnetic =>
            [
                ShipOrientationSource.CogMagnetic,
                ShipOrientationSource.CogTrue,
                ShipOrientationSource.HeadingMagnetic,
                ShipOrientationSource.HeadingTrue,
            ],
            _ =>
            [
                ShipOrientationSource.HeadingTrue,
                ShipOrientationSource.HeadingMagnetic,
                ShipOrientationSource.CogTrue,
                ShipOrientationSource.CogMagnetic,
            ],
        };
}
