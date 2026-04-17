namespace OnaPlotter.Services;

/// <summary>
/// Abstract key-value persistence. Production uses localStorage via JS interop;
/// tests inject an in-memory fake.
/// </summary>
public interface IKeyValueStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}
