using System.Globalization;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests.Utilities;

public class SignalkTimestampTests
{
    [Test]
    [Arguments("2025-01-01T12:00:00.000Z", 0L)]
    [Arguments("2025-01-01T12:00:00Z", 0L)]
    [Arguments("2025-01-01T12:00:00.5Z", 5_000_000L)]
    [Arguments("2025-01-01T12:00:00.1234567Z", 1_234_567L)]
    [Arguments("2025-01-01T12:00:00.123456789Z", 1_234_567L)]
    [Arguments("2025-01-01T12:00:00.123456789+00:00", 1_234_567L)]
    [Arguments("2025-01-01T14:00:00.000+02:00", 0L)]
    [Arguments("2025-01-01T07:00:00-05:00", 0L)]
    [Arguments("2025-01-01T12:00:00", 0L)]
    public async Task Parses_SignalK_Iso8601_ToUtc(string text, long extraTicks)
    {
        // signalk-server stamps with Date.toISOString(); plugins vary
        // the fraction and offset. All of these mean 12:00 UTC.
        await Assert.That(SignalkTimestamp.TryParseUtc(text, out var utc)).IsTrue();
        await Assert.That(utc).IsEqualTo(TestClock.FixedUtcNow.AddTicks(extraTicks));
        await Assert.That(utc.Kind).IsEqualTo(DateTimeKind.Utc);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("01/01/2025 12:00")]
    [Arguments("2025-01-01")]
    [Arguments("2025-13-01T12:00:00Z")]
    [Arguments("2025-01-01T12:00:00.000ZZ")]
    [Arguments("yesterday")]
    public async Task Rejects_NonIso(string? text)
    {
        await Assert.That(SignalkTimestamp.TryParseUtc(text, out var utc)).IsFalse();
        await Assert.That(utc).IsEqualTo(default(DateTime));
    }

    [Test]
    public async Task IgnoresCurrentCulture()
    {
        // A helm iPad set to Thai uses the Buddhist calendar; culture-
        // aware parsing would land 543 years off.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            await Assert.That(SignalkTimestamp.TryParseUtc("2025-01-01T12:00:00.000Z", out var utc)).IsTrue();
            await Assert.That(utc).IsEqualTo(TestClock.FixedUtcNow);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
