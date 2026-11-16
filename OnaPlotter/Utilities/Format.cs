using System.Globalization;

namespace OnaPlotter.Utilities;

/// <summary>
/// Display formatting shared by HUD cards, panels and pages, so one
/// reading renders the same way wherever the helm sees it.
/// </summary>
public static class Format
{
    /// <summary>Conversion factor: meters per second to knots.</summary>
    public const double MsToKnots = 1.94384;

    /// <summary>Conversion factor: radians to degrees.</summary>
    public const double RadToDeg = 180.0 / Math.PI;

    /// <summary>Meters per nautical mile.</summary>
    public const double MetersPerNm = 1852.0;

    /// <summary>Formats a speed value in m/s as knots, one decimal. Returns "--" for null.</summary>
    public static string Speed(double? ms) =>
        ms is null ? "--" : (ms.Value * MsToKnots).ToString("F1");

    /// <summary>Formats an angle in radians as degrees (0-360), no decimals. Returns "--" for null.</summary>
    public static string Degrees(double? rad)
    {
        if (rad is null) return "--";
        double deg = rad.Value * RadToDeg;
        if (deg < 0) deg += 360;
        return deg.ToString("F0");
    }

    /// <summary>Formats a distance in meters as nautical miles (2 decimals under 10nm, 1 above).</summary>
    public static string Nm(double? meters)
    {
        if (meters is null) return "--";
        double nm = meters.Value / MetersPerNm;
        return nm < 10 ? nm.ToString("F2") : nm.ToString("F1");
    }

    /// <summary>Formats a distance in meters: meters under 1000, nautical miles above.</summary>
    public static string MetersDual(double? m)
    {
        if (m is null) return "--";
        return m.Value < 1000 ? $"{m.Value:F0}m" : $"{(m.Value / MetersPerNm):F2}nm";
    }

    /// <summary>Formats a depth in meters, one decimal. Returns "--" for null.</summary>
    public static string Depth(double? m) =>
        m is null ? "--" : m.Value.ToString("F1");

    /// <summary>Formats remaining time in seconds as "1h23m" or "5m30s".</summary>
    public static string TimeToGo(double? seconds)
    {
        if (seconds is null) return "--";
        var ts = TimeSpan.FromSeconds(seconds.Value);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h{ts.Minutes:D2}m"
            : $"{ts.Minutes}m{ts.Seconds:D2}s";
    }

    /// <summary>Formats ETA as "HH:mm" when it falls on the same local
    /// day as <paramref name="nowLocal"/>, "dd.MM HH:mm" otherwise.
    /// <paramref name="nowLocal"/> is the caller's local wall-clock instant
    /// (<see cref="TimeProvider.GetLocalNow"/>).
    /// Example: nowLocal 2026-06-01 22:00, ttg 3600 s -> "23:00";
    /// ttg 10800 s -> "02.06 01:00".</summary>
    public static string Eta(double? ttgSeconds, DateTimeOffset nowLocal)
    {
        if (ttgSeconds is null) return "--";
        var eta = nowLocal.AddSeconds(ttgSeconds.Value);
        return eta.Date == nowLocal.Date
            ? eta.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : eta.ToString("dd.MM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Formats a lat/lon pair as DMS with hemisphere
    /// indicators: "47.50000&deg;N 8.50000&deg;W". Five decimals on
    /// the degrees ~= 1m precision. The JS popups render the same
    /// string with <c>format.js latLonDms</c>; both are pinned by the
    /// same vectors (FormatTests / format.test.js).</summary>
    public static string LatLonDms(double lat, double lon)
    {
        char ns = lat >= 0 ? 'N' : 'S';
        char ew = lon >= 0 ? 'E' : 'W';
        return $"{Math.Abs(lat).ToString("F5", System.Globalization.CultureInfo.InvariantCulture)}°{ns} " +
               $"{Math.Abs(lon).ToString("F5", System.Globalization.CultureInfo.InvariantCulture)}°{ew}";
    }

    /// <summary>Formats XTE with port/starboard suffix. SignalK convention: positive = starboard.</summary>
    public static string Xte(double? meters)
    {
        if (meters is null) return "--";
        double abs = Math.Abs(meters.Value);
        string side = meters > 0 ? " S" : meters < 0 ? " P" : "";
        return abs < 1000 ? $"{abs:F0}m{side}" : $"{(abs / MetersPerNm):F2}nm{side}";
    }

    /// <summary>XTE severity class name for CSS color coding.</summary>
    public static string XteClass(double? meters) => meters switch
    {
        null => "",
        var x when Math.Abs(x.Value) < 50 => "xte-ok",
        var x when Math.Abs(x.Value) < 200 => "xte-warn",
        _ => "xte-danger"
    };

    /// <summary>Depth severity class name for CSS color coding.</summary>
    public static string DepthClass(double? depth) => depth switch
    {
        null => "",
        < 3 => "depth-danger",
        < 8 => "depth-warn",
        _ => "depth-ok"
    };

    /// <summary>Whether a depth is tinted amber or red by
    /// <see cref="DepthClass"/>. Drives the shape cue (warning glyph)
    /// next to the number, so the shallow-water signal doesn't rely on
    /// hue alone.</summary>
    public static bool IsShallow(double? depth) => depth is < 8;

    /// <summary>Decimal-degree position with hemisphere letters, as the
    /// route and polygon edit panels list vertices. Input is a
    /// <c>[lat, lon, ...]</c> pair; empty when it has fewer than two
    /// elements.
    /// <para>Example: <c>[48.21444, -11.57532]</c> becomes
    /// <c>"48.2144° N, 11.5753° W"</c>.</para></summary>
    public static string CoordDecimal(double[]? latLon)
    {
        if (latLon is not { Length: >= 2 }) return "";
        double lat = latLon[0], lon = latLon[1];
        string ns = lat >= 0 ? "N" : "S";
        string ew = lon >= 0 ? "E" : "W";
        return string.Create(CultureInfo.InvariantCulture,
            $"{Math.Abs(lat):F4}\u00B0 {ns}, {Math.Abs(lon):F4}\u00B0 {ew}");
    }

    /// <summary>Radar range label from metres. Short ranges read better
    /// in metres ("250 m" vs "0.1 nm"); from 1000 m up, nautical miles
    /// with one decimal for open-water scales.</summary>
    public static string RadarRange(int meters) =>
        meters < 1000
            ? string.Create(CultureInfo.InvariantCulture, $"{meters} m")
            : string.Create(CultureInfo.InvariantCulture, $"{meters / MetersPerNm:F1} nm");

    /// <summary>Value for an HTML <c>datetime-local</c> input:
    /// <c>yyyy-MM-ddTHH:mm</c>. Any other shape (seconds, offset) renders
    /// the input blank, and the invariant culture keeps a de-CH locale
    /// from producing <c>05.06.2024</c>.</summary>
    public static string DateTimeLocalInput(DateTime t) =>
        t.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
}
