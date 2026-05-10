using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Utilities;

/// <summary>
/// Helm-picked COG source. Two orthogonal axes:
///   <list type="bullet">
///     <item><b>True vs Magnetic</b> - which SignalK field is the
///       primary read (<c>navigation.courseOverGroundTrue</c> vs
///       <c>navigation.courseOverGroundMagnetic</c>).</item>
///     <item><b>Smoothed vs Realtime</b> - the smoothed variant
///       reads from <see cref="INavigationAverages.CogMean30Sec"/>
///       (30s rolling mean, weighted by motion) so the value stops
///       twitching with every wave; realtime reads the raw delta
///       so a sudden slewing turn shows immediately.</item>
///   </list>
///
/// <para>Used by two distinct settings on <see cref="IAppSettings"/>:
/// <see cref="IAppSettings.CogReadoutSource"/> drives the HUD's
/// numerical COG readout (helm wants stability for a number they're
/// reading); <see cref="IAppSettings.OwnCogVectorSource"/> drives
/// the on-map COG vector line (helm may want realtime here for
/// immediate steering feedback).</para>
/// </summary>
public enum CogSource
{
    TrueSmoothed,
    TrueRealtime,
    MagneticSmoothed,
    MagneticRealtime,
}

/// <summary>
/// Persisted-string &lt;-&gt; <see cref="CogSource"/> mapping +
/// resolver that picks the angle from <see cref="NavigationData"/>
/// + <see cref="INavigationAverages"/> based on the helm's pick.
///
/// <para>Fallback chain mirrors <see cref="ShipOrientationResolver"/>:
/// helm choice first, then the same axis-pair with the other
/// variant (smoothed -&gt; realtime / true -&gt; magnetic), then the
/// remaining slots. Boats publishing only one of the two SK paths
/// still get a number; the picker stays glanceable instead of
/// vanishing because the helm picked a field this server doesn't
/// publish.</para>
/// </summary>
public static class CogSourceResolver
{
    /// <summary>Persisted-string default (<c>"trueSmoothed"</c>).
    /// Smoothed is the helm-friendly default for readouts; the on-
    /// map vector setting can override per-helm.</summary>
    public const string DefaultSetting = "trueSmoothed";

    /// <summary>Persisted-string -&gt; enum mapping. Unknown strings
    /// fall back to <see cref="CogSource.TrueSmoothed"/> rather than
    /// throw - a corrupt localStorage value should never crash the
    /// chart.</summary>
    public static CogSource Parse(string? raw) =>
        (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "truerealtime" or "truelive" => CogSource.TrueRealtime,
            "magneticsmoothed" or "magsmoothed" => CogSource.MagneticSmoothed,
            "magneticrealtime" or "magrealtime" or "maglive" => CogSource.MagneticRealtime,
            _ => CogSource.TrueSmoothed,
        };

    /// <summary>Round-trip the enum to its persisted-string form.</summary>
    public static string ToSetting(CogSource s) => s switch
    {
        CogSource.TrueRealtime => "trueRealtime",
        CogSource.MagneticSmoothed => "magneticSmoothed",
        CogSource.MagneticRealtime => "magneticRealtime",
        _ => "trueSmoothed",
    };

    /// <summary>True when this source picks a magnetic field. Helper
    /// for callers that want to flip the boolean
    /// <see cref="NavigationData.PreferMagneticCourse"/> (kept around
    /// for back-compat readers) from a CogSource pick.</summary>
    public static bool IsMagnetic(CogSource s) =>
        s is CogSource.MagneticSmoothed or CogSource.MagneticRealtime;

    /// <summary>True when this source asks for the smoothed variant.</summary>
    public static bool IsSmoothed(CogSource s) =>
        s is CogSource.TrueSmoothed or CogSource.MagneticSmoothed;

    /// <summary>
    /// Resolve the COG angle (radians) using the helm's pick, with
    /// a deterministic fallback chain. Returns null when none of the
    /// candidate fields are published.
    /// </summary>
    /// <param name="source">Helm-picked source.</param>
    /// <param name="data">Live navigation snapshot (for raw fields).</param>
    /// <param name="smoothedTrue">30s mean of true-COG. Null when
    /// the smoothing buffer hasn't filled yet (boot warmup) or when
    /// the boat has been stationary long enough for samples to age
    /// out. Falls back to <paramref name="data"/>.CourseOverGroundTrue
    /// in that case so the chart isn't blank.</param>
    /// <param name="smoothedMagnetic">30s mean of magnetic-COG. Same
    /// null semantics as <paramref name="smoothedTrue"/>.</param>
    public static double? Resolve(
        CogSource source,
        NavigationData data,
        double? smoothedTrue,
        double? smoothedMagnetic)
    {
        double? Pick(CogSource s) => s switch
        {
            CogSource.TrueSmoothed => smoothedTrue ?? data.CourseOverGroundTrue,
            CogSource.TrueRealtime => data.CourseOverGroundTrue,
            CogSource.MagneticSmoothed => smoothedMagnetic ?? data.CourseOverGroundMagnetic,
            CogSource.MagneticRealtime => data.CourseOverGroundMagnetic,
            _ => null,
        };

        foreach (var step in FallbackChain(source))
        {
            if (Pick(step) is double v) return v;
        }
        return null;
    }

    /// <summary>Pick order: helm choice -&gt; same axis other variant
    /// (smoothed flips to realtime / true flips to magnetic) -&gt;
    /// remaining slots. Encodes "stay close to the helm's intent"
    /// for boats that only publish one SK path.</summary>
    private static IEnumerable<CogSource> FallbackChain(CogSource pick) =>
        pick switch
        {
            CogSource.TrueSmoothed =>
            [
                CogSource.TrueSmoothed,
                CogSource.TrueRealtime,
                CogSource.MagneticSmoothed,
                CogSource.MagneticRealtime,
            ],
            CogSource.TrueRealtime =>
            [
                CogSource.TrueRealtime,
                CogSource.TrueSmoothed,
                CogSource.MagneticRealtime,
                CogSource.MagneticSmoothed,
            ],
            CogSource.MagneticSmoothed =>
            [
                CogSource.MagneticSmoothed,
                CogSource.MagneticRealtime,
                CogSource.TrueSmoothed,
                CogSource.TrueRealtime,
            ],
            CogSource.MagneticRealtime =>
            [
                CogSource.MagneticRealtime,
                CogSource.MagneticSmoothed,
                CogSource.TrueRealtime,
                CogSource.TrueSmoothed,
            ],
            _ =>
            [
                CogSource.TrueSmoothed,
                CogSource.TrueRealtime,
                CogSource.MagneticSmoothed,
                CogSource.MagneticRealtime,
            ],
        };
}
