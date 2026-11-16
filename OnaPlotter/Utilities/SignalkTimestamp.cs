using System.Globalization;

namespace OnaPlotter.Utilities;

/// <summary>
/// Parses the ISO-8601 timestamps SignalK puts on delta updates and
/// notification values. signalk-server stamps them with JavaScript's
/// <c>Date.toISOString()</c>; plugins occasionally drop the fraction or
/// send an explicit offset. Exact formats keep this off the
/// culture-sensitive, heuristic <see cref="DateTime.TryParse(string?, out DateTime)"/>
/// path, which runs once per AIS update.
/// <code>
/// "2025-01-01T12:00:00.000Z"       -> 2025-01-01 12:00:00 UTC
/// "2025-01-01T12:00:00Z"           -> 2025-01-01 12:00:00 UTC
/// "2025-01-01T12:00:00.1234567Z"   -> 2025-01-01 12:00:00.1234567 UTC
/// "2025-01-01T12:00:00.123456789Z" -> 2025-01-01 12:00:00.1234567 UTC (excess digits dropped)
/// "2025-01-01T14:00:00.000+02:00"  -> 2025-01-01 12:00:00 UTC
/// "2025-01-01T12:00:00"            -> 2025-01-01 12:00:00 UTC (no offset: assumed UTC)
/// "01/01/2025 12:00", "", null     -> false
/// </code>
/// </summary>
public static class SignalkTimestamp
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:ssK",
    ];

    /// <summary>Parses <paramref name="text"/> into a UTC
    /// <see cref="DateTime"/> (<see cref="DateTimeKind.Utc"/>).</summary>
    public static bool TryParseUtc(string? text, out DateTime utc)
    {
        if (!string.IsNullOrEmpty(text)
            && DateTimeOffset.TryParseExact(TrimFraction(text), Formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
        {
            utc = parsed.UtcDateTime;
            return true;
        }
        utc = default;
        return false;
    }

    // .NET resolves 7 fractional digits (100 ns); N2K gateways that
    // forward nanosecond clocks send 9. Drop the excess digits:
    // "2025-01-01T12:00:00.123456789Z" -> "2025-01-01T12:00:00.1234567Z".
    private const int SecondsDotIndex = 19; // "yyyy-MM-ddTHH:mm:ss" is 19 chars
    private const int MaxFractionDigits = 7;

    private static string TrimFraction(string text)
    {
        if (text.Length <= SecondsDotIndex + 1 + MaxFractionDigits || text[SecondsDotIndex] != '.')
            return text;
        int end = SecondsDotIndex + 1;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        int digits = end - SecondsDotIndex - 1;
        return digits <= MaxFractionDigits
            ? text
            : string.Concat(text.AsSpan(0, SecondsDotIndex + 1 + MaxFractionDigits), text.AsSpan(end));
    }
}
