// Nautical formatting utilities. Sailors use DD°MM.MMM' notation, not decimal degrees.
// Example: 47.390933 -> 47°23.456'N

namespace OnaPlotter.Models;

/// <summary>
/// Nautical formatting utilities. Converts decimal degrees to the DD°MM.MMM'
/// notation used by sailors (e.g. 47.390933 → 47°23.456'N).
/// </summary>
public static class NauticalFormat
{
    public static string FormatLat(double? deg)
    {
        if (deg is null) return "--";
        var d = Math.Abs(deg.Value);
        int degrees = (int)d;
        double minutes = (d - degrees) * 60;
        char hemisphere = deg.Value >= 0 ? 'N' : 'S';
        return $"{degrees:D2}\u00b0{minutes:00.000}'{hemisphere}";
    }

    public static string FormatLon(double? deg)
    {
        if (deg is null) return "--";
        var d = Math.Abs(deg.Value);
        int degrees = (int)d;
        double minutes = (d - degrees) * 60;
        char hemisphere = deg.Value >= 0 ? 'E' : 'W';
        return $"{degrees:D3}\u00b0{minutes:00.000}'{hemisphere}";
    }

    public static string FormatPosition(double? lat, double? lon)
    {
        if (lat is null || lon is null) return "--";
        return $"{FormatLat(lat)}  {FormatLon(lon)}";
    }
}
