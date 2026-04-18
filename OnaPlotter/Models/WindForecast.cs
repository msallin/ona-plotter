namespace OnaPlotter.Models;

/// <summary>
/// An hourly wind sample returned by a forecast provider. Direction is
/// true meteorological: the degrees FROM which the wind blows, 0 = N.
/// </summary>
public readonly record struct WindSample(
    DateTime ValidTime,
    double DirectionDeg,
    double SpeedKn);

/// <summary>
/// Time series of hourly wind samples at a single lat/lon. Used as the
/// input to the isochrone router; we look up the sample whose ValidTime
/// bracket contains the query time via <see cref="At"/>.
/// </summary>
public sealed record WindForecast(
    double Latitude,
    double Longitude,
    IReadOnlyList<WindSample> Hours)
{
    /// <summary>
    /// Returns the sample closest in time to <paramref name="time"/>, or
    /// null if the series is empty. Falls back to clamping at the
    /// boundaries when the query is outside the forecast range.
    /// </summary>
    public WindSample? At(DateTime time)
    {
        if (Hours.Count == 0) return null;
        if (time <= Hours[0].ValidTime) return Hours[0];
        if (time >= Hours[^1].ValidTime) return Hours[^1];

        // Linear scan - forecasts are small (24-72 hours typically) so no
        // need for a binary search. Keep it simple.
        for (int i = 0; i < Hours.Count - 1; i++)
        {
            if (Hours[i].ValidTime <= time && time < Hours[i + 1].ValidTime)
            {
                // Pick the closer of the two bracketing samples.
                var dL = time - Hours[i].ValidTime;
                var dR = Hours[i + 1].ValidTime - time;
                return dL <= dR ? Hours[i] : Hours[i + 1];
            }
        }
        return Hours[^1];
    }
}
