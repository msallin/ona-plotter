namespace OnaPlotter.Services;

/// <summary>
/// Moves values stored under a renamed <see cref="IKeyValueStore"/> key to
/// its successor so an upgrading helm keeps their settings.
/// <para>
/// Per pair: when the legacy key holds a value, copy it to the current key
/// unless the current key already has one (a value written under the new
/// name always wins), then remove the legacy key. A boot with nothing to
/// migrate costs one read per pair.
/// </para>
/// <para>
/// Storage failures (private browsing, quota, page teardown) abandon the
/// pair silently: the legacy key stays put and the next boot retries, while
/// this boot loads defaults exactly as it would for any unreadable key.
/// </para>
/// </summary>
public static class KeyValueStoreMigration
{
    public static async Task MigrateRenamedAsync(
        IKeyValueStore store, IEnumerable<(string Legacy, string Current)> renamed)
    {
        foreach (var (legacy, current) in renamed)
        {
            try
            {
                var legacyValue = await store.GetAsync(legacy);
                if (legacyValue is null) continue;
                if (await store.GetAsync(current) is null)
                    await store.SetAsync(current, legacyValue);
                await store.RemoveAsync(legacy);
            }
            catch (Microsoft.JSInterop.JSException) { }
            catch (Microsoft.JSInterop.JSDisconnectedException) { }
        }
    }
}
