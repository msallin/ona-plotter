namespace OnaPlotter.Models;

/// <summary>
/// A buddy entry from sbender9/signalk-buddylist-plugin. The URN is a SignalK
/// vessel identifier (typically <c>urn:mrn:imo:mmsi:&lt;MMSI&gt;</c>); the name
/// is the friendly label the captain assigned.
/// </summary>
public sealed record SignalkBuddy(string Urn, string Name)
{
    /// <summary>
    /// Returns just the MMSI portion of the URN, or null if the URN doesn't
    /// carry an MMSI. Convenient for matching against AIS vessel MMSIs.
    /// </summary>
    public string? Mmsi
    {
        get
        {
            const string marker = "mmsi:";
            int idx = Urn.IndexOf(marker, StringComparison.Ordinal);
            return idx >= 0 ? Urn[(idx + marker.Length)..] : null;
        }
    }
}
