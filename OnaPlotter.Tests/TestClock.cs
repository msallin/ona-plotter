namespace OnaPlotter.Tests;

/// <summary>
/// Shared fixed-clock seed for tests that need a deterministic <c>DateTime</c>.
/// Replaces direct <c>DateTime.UtcNow</c> reads in test scope so cases that
/// reason about relative deltas (alarm dwell windows, snooze cooldowns,
/// MOB retry backoffs) are <em>Repeatable</em> per the FIRST test rules -
/// the same test run on a slow CI agent during a DST flip or leap second
/// produces byte-identical timestamps to a fast laptop run.
/// <para>
/// Pick a date well inside the historical UTC offset table (2025-01-01
/// noon UTC) so any UTC-to-local conversion done by the SUT in passing
/// resolves through a stable offset on every machine. The value is
/// arbitrary - tests should never depend on its exact bits, only on
/// the relative deltas they build from it.
/// </para>
/// </summary>
public static class TestClock
{
    /// <summary>Fixed UTC seed: 2025-01-01T12:00:00Z. Hard-coded as
    /// <see cref="DateTimeKind.Utc"/> so an accidental
    /// <see cref="TimeZoneInfo.ConvertTimeFromUtc"/> call in the SUT
    /// doesn't double-shift the value.</summary>
    public static readonly DateTime FixedUtcNow =
        new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
}
