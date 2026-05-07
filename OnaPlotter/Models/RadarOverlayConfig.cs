namespace OnaPlotter.Models;

/// <summary>
/// Bounds the radar overlay applies to server-supplied geometry
/// before allocating canvas / LUT memory. A hostile or buggy SK
/// plugin could otherwise return absurd <c>spokesPerRevolution</c> /
/// <c>maxSpokeLen</c> values and the JS layer would obediently try to
/// allocate the resulting (2*maxLen)^2 ImageData; on a phone helm
/// this either crashes the tab or drags the whole page down. The
/// numbers here are well above the largest real-world radar
/// (Navico HALO at 2048/1024) but bounded enough that worst-case
/// allocation stays inside a single tab's recoverable budget
/// (~256 MB ImageData, ~32 MB LUT).
/// </summary>
public static class RadarOverlayLimits
{
    public const int MinSpokesPerRevolution = 64;
    public const int MaxSpokesPerRevolution = 8192;
    public const int MinSpokeLength = 64;
    public const int MaxSpokeLength = 4096;

    /// <summary>Default when neither capabilities nor the radar list
    /// reports a value - matches Navico HALO native geometry.</summary>
    public const int DefaultSpokesPerRevolution = 2048;
    public const int DefaultSpokeLength = 1024;

    /// <summary>Sane fallback range in metres when the radar omits one
    /// from /radars (a reporting bug, not a normal state). Picked low
    /// enough to be useful at harbour scale rather than a guess at the
    /// open-water default.</summary>
    public const int DefaultRangeMetres = 1000;

    /// <summary>Clamp <paramref name="value"/> into the spokes-per-rev
    /// envelope. Returns the default when null.</summary>
    public static int ClampSpokesPerRevolution(int? value) =>
        value is int v
            ? System.Math.Clamp(v, MinSpokesPerRevolution, MaxSpokesPerRevolution)
            : DefaultSpokesPerRevolution;

    /// <summary>Clamp <paramref name="value"/> into the max-spoke-length
    /// envelope. Returns the default when null.</summary>
    public static int ClampSpokeLength(int? value) =>
        value is int v
            ? System.Math.Clamp(v, MinSpokeLength, MaxSpokeLength)
            : DefaultSpokeLength;
}
