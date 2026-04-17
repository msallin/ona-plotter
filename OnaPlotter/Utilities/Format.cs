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
