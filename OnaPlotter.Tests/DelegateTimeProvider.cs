namespace OnaPlotter.Tests;

/// <summary>
/// <see cref="TimeProvider"/> over a test's own clock lambda, for
/// fixtures that already drive time through a mutable field
/// (<c>clock.Now = ...</c>) rather than <c>FakeTimeProvider.Advance</c>.
/// The lambda must return UTC.
/// </summary>
internal sealed class DelegateTimeProvider(Func<DateTime> utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() =>
        new(DateTime.SpecifyKind(utcNow(), DateTimeKind.Utc));
}
