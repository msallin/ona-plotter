using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>Builds an <see cref="AlarmManager"/> with the fakes every
/// test needs (in-memory storage, <see cref="FakeSettings"/>) so a
/// fixture only names what it cares about. The manager itself requires
/// all four dependencies.</summary>
internal static class AlarmManagerFactory
{
    public static AlarmManager Create(IEnumerable<IAlarmRule> rules, TimeProvider time,
        FakeSettings? settings = null, IKeyValueStore? kv = null)
        => new(rules, kv ?? new InMemoryKeyValueStore(), settings ?? new FakeSettings(), time);
}

/// <summary>Dictionary-backed <see cref="IKeyValueStore"/>.</summary>
internal sealed class InMemoryKeyValueStore : IKeyValueStore
{
    private readonly Dictionary<string, string> _d = [];
    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_d.TryGetValue(key, out var v) ? v : null);
    public Task SetAsync(string key, string value, CancellationToken ct = default)
    { _d[key] = value; return Task.CompletedTask; }
    public Task RemoveAsync(string key, CancellationToken ct = default)
    { _d.Remove(key); return Task.CompletedTask; }
}
