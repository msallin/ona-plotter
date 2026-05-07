namespace OnaPlotter.Utilities;

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

    /// <summary>Formats ETA as "HH:mm" today, "dd.MM HH:mm" otherwise.</summary>
    public static string Eta(double? ttgSeconds)
    {
        if (ttgSeconds is null) return "--";
        var eta = DateTime.Now.AddSeconds(ttgSeconds.Value);
        return eta.Date == DateTime.Today ? eta.ToString("HH:mm") : eta.ToString("dd.MM HH:mm");
    }

    /// <summary>Formats elapsed-since-MOB-raise as "T+5s" / "T+12m"
    /// / "T+1h23m". Pure formatter - caller passes the seconds
    /// since the casualty was raised. JS layer uses this via the
    /// mirrored helper in <c>format.js</c>; tests live here to pin
    /// the exact strings.</summary>
    public static string MobElapsed(int seconds)
    {
        if (seconds < 0) seconds = 0;
        if (seconds < 60) return $"T+{seconds}s";
        int min = seconds / 60;
        if (min < 60) return $"T+{min}m";
        int hr = min / 60;
        return $"T+{hr}h{min % 60}m";
    }

    /// <summary>Formats a lat/lon pair as DMS with hemisphere
    /// indicators: "47.50000&deg;N 8.50000&deg;W". Five decimals on
    /// the degrees ~= 1m precision - enough for a chart popup. Used
    /// by waypoint / note / MOB popups in JS via the mirrored helper.</summary>
    public static string LatLonDms(double lat, double lon)
    {
        char ns = lat >= 0 ? 'N' : 'S';
        char ew = lon >= 0 ? 'E' : 'W';
        return $"{Math.Abs(lat).ToString("F5", System.Globalization.CultureInfo.InvariantCulture)}°{ns} " +
               $"{Math.Abs(lon).ToString("F5", System.Globalization.CultureInfo.InvariantCulture)}°{ew}";
    }

    /// <summary>Formats a radius in nautical miles for an on-chart
    /// ring label: "0.5 nm" / "1.5 nm" / "5 nm". Two decimals below
    /// 1 nm so 0.5 doesn't round to "1"; one decimal at 1-9.99;
    /// whole number above 10. Trailing zeros are stripped ("0.50"
    /// -&gt; "0.5", "1.0" -&gt; "1") to keep chart labels compact.
    /// Returns empty for non-finite or non-positive input.</summary>
    public static string RangeRingLabel(double nm)
    {
        if (!double.IsFinite(nm) || nm <= 0) return string.Empty;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string digits;
        if (nm < 1)
        {
            // F2 then strip trailing zeros (and the dot if all zeros after).
            digits = nm.ToString("F2", inv).TrimEnd('0').TrimEnd('.');
        }
        else if (nm < 10)
        {
            // F1 then strip a trailing ".0" - a non-zero tenths digit
            // (e.g. "1.5") survives the strip.
            digits = nm.ToString("F1", inv);
            if (digits.EndsWith(".0", StringComparison.Ordinal))
            {
                digits = digits[..^2];
            }
        }
        else
        {
            // AwayFromZero matches JS's Math.round (50.5 -> 51) so the
            // C# canonical and the format.js mirror produce identical
            // strings on the .5 boundary.
            digits = ((long)Math.Round(nm, MidpointRounding.AwayFromZero)).ToString(inv);
        }
        return $"{digits} nm";
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
}
