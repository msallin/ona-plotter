namespace OnaPlotter.Utilities;

/// <summary>
/// Cross-track-error severity classifier. Drives the perpendicular XTE
/// tick colour drawn on the map (green / amber / red) and matches the
/// thresholds the Settings legend documents to the helm. Pure
/// classification with no rendering side-effects so the JS layer can
/// render the result without re-implementing the bands.
/// </summary>
public static class Xte
{
    /// <summary>Below this absolute XTE (metres) the boat is "on the line".</summary>
    public const double OnLineThresholdMeters = 50.0;

    /// <summary>Below this absolute XTE (metres) the boat is "drifting".
    /// Above it the boat is "off course".</summary>
    public const double OffCourseThresholdMeters = 200.0;

    /// <summary>
    /// XTE severity. Maps onto the same ok / warn / danger palette used
    /// by the alarm severity tokens.
    /// </summary>
    public enum Severity { OnLine, Drifting, OffCourse }

    /// <summary>
    /// Classifies a signed cross-track error (metres) into a severity
    /// band. Sign is irrelevant - the bands are symmetric. Pass null /
    /// NaN through as <see cref="Severity.OnLine"/> so the caller can
    /// always paint a default tick rather than branching for "no XTE".
    /// </summary>
    public static Severity Classify(double? xteMeters)
    {
        if (xteMeters is null || !double.IsFinite(xteMeters.Value)) return Severity.OnLine;
        double abs = System.Math.Abs(xteMeters.Value);
        if (abs < OnLineThresholdMeters) return Severity.OnLine;
        if (abs < OffCourseThresholdMeters) return Severity.Drifting;
        return Severity.OffCourse;
    }
}
