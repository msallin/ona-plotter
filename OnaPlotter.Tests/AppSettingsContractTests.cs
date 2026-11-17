using System.Reflection;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Reflection-driven pins for the parts of <see cref="AppSettingsService"/>
/// that are easy to break while changing how settings are declared: which
/// setters raise <c>OnSettingsChanged</c>, that the pre-load and post-load
/// defaults agree, and that every persisted key is read at startup.
/// </summary>
public class AppSettingsContractTests
{
    private sealed class RecordingKv : IKeyValueStore
    {
        public readonly Dictionary<string, string> Data = [];
        public readonly HashSet<string> Reads = [];
        public readonly Dictionary<string, string> Writes = [];
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            Reads.Add(key);
            return Task.FromResult(Data.TryGetValue(key, out var v) ? v : null);
        }
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        { Data[key] = value; Writes[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key, CancellationToken ct = default)
        { Data.Remove(key); return Task.CompletedTask; }
    }

    // Setters that persist without raising OnSettingsChanged. Each is
    // deliberate: the Map page owns the live value and applies it itself
    // (follow / layers toggles, server-track options), the value feeds no
    // other subscriber (enabled chart / route ids, stored credentials,
    // map view), or the call is a one-shot flag (charts seeded).
    private static readonly string[] QuietSetters =
    [
        "MarkChartsSeededAsync",
        "MarkManualNightToggleAsync",
        "ClearManualNightOverrideAsync",
        "SetFollowBoatAsync",
        "SetLaylinesVisibleAsync",
        "SetShipLinesVisibleAsync",
        "SetServerTrackVisibleAsync",
        "SetServerTrackDurationAsync",
        "SetServerTrackResolutionAsync",
        "SetServerTrackWithinBoundsAsync",
        "SetEnabledChartsAsync",
        "SetEnabledRoutesAsync",
        "SetStoredUsernameAsync",
        "SetStoredPasswordAsync",
    ];

    private static IEnumerable<MethodInfo> AllSetters() =>
        typeof(AppSettingsService).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => (m.Name.StartsWith("Set") || m.Name.StartsWith("Mark") || m.Name.StartsWith("Clear"))
                && m.Name.EndsWith("Async"))
            .Where(m => m.GetParameters().Length <= 1 && m.ReturnType == typeof(Task));

    private static object Sample(Type t) =>
        t == typeof(bool) ? true
        : t == typeof(double) ? 12.5
        : t == typeof(int) ? 3
        : t == typeof(string) ? "x"
        : t == typeof(IEnumerable<string>) ? new[] { "a" }
        : throw new NotSupportedException(t.Name);

    [Test]
    public async Task EverySetter_RaisesOnSettingsChanged_ExceptTheQuietOnes()
    {
        var mismatches = new List<string>();
        foreach (var m in AllSetters())
        {
            var svc = new AppSettingsService(new RecordingKv());
            await svc.InitializeAsync();
            int fired = 0;
            svc.OnSettingsChanged += () => fired++;
            var args = m.GetParameters().Length == 0 ? [] : new[] { Sample(m.GetParameters()[0].ParameterType) };

            await (Task)m.Invoke(svc, args)!;

            bool quiet = QuietSetters.Contains(m.Name);
            if (quiet && fired != 0) mismatches.Add($"{m.Name} should be quiet");
            if (!quiet && fired == 0) mismatches.Add($"{m.Name} should notify");
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task EveryQuietSetterName_IsARealSetter()
    {
        var names = AllSetters().Select(m => m.Name).ToHashSet();
        await Assert.That(QuietSetters.Where(n => !names.Contains(n)).ToArray()).IsEmpty();
    }

    [Test]
    public async Task Defaults_BeforeAndAfterInitialize_Agree()
    {
        var svc = new AppSettingsService(new RecordingKv());
        var props = typeof(AppSettingsService).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0
                && (p.PropertyType.IsValueType || p.PropertyType == typeof(string)))
            .ToArray();
        var before = props.ToDictionary(p => p.Name, p => p.GetValue(svc));

        await svc.InitializeAsync();

        var drift = props.Where(p => !Equals(before[p.Name], p.GetValue(svc)))
            .Select(p => $"{p.Name}: {before[p.Name]} -> {p.GetValue(svc)}").ToArray();
        await Assert.That(drift).IsEmpty();
    }

    [Test]
    public async Task Initialize_ReadsEveryPersistedKey()
    {
        var kv = new RecordingKv();
        var svc = new AppSettingsService(kv);

        await svc.InitializeAsync();

        var keys = typeof(SettingKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Where(f => !f.Name.StartsWith("Legacy"))
            .Select(f => (f.Name, Key: (string)f.GetRawConstantValue()!))
            .Where(k => !NotOwnedBySettings.Contains(k.Name))
            .ToArray();
        var unread = keys.Where(k => !kv.Reads.Contains(k.Key)).Select(k => k.Name).ToArray();
        await Assert.That(string.Join(", ", unread)).IsEqualTo("");
    }

    // SettingKeys entries owned by other services; the settings store never
    // reads them.
    private static readonly HashSet<string> NotOwnedBySettings = new(StringComparer.Ordinal) { "Polar" };

    private static object[] Samples(Type t) =>
        t == typeof(bool) ? [true, false]
        : t == typeof(double) ? [12.5, -7.3, 99999.9, double.NaN]
        : t == typeof(int) ? [3, -5, 99999]
        : t == typeof(string) ? ["x", "headingMagnetic", ""]
        : t == typeof(IEnumerable<string>) ? [new[] { "a", "b" }]
        : throw new NotSupportedException(t.Name);

    /// <summary>Every persisted (key, stored text) pair each setter writes
    /// for in-range, out-of-range and degenerate inputs.</summary>
    internal static async Task<string> WriteLog()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in AllSetters().OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var ps = m.GetParameters();
            foreach (var arg in ps.Length == 0 ? [null!] : Samples(ps[0].ParameterType))
            {
                var kv = new RecordingKv();
                var svc = new AppSettingsService(kv, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(TestClock.FixedUtcNow));
                await svc.InitializeAsync();
                kv.Writes.Clear();
                await (Task)m.Invoke(svc, ps.Length == 0 ? [] : [arg])!;
                sb.Append(m.Name).Append('(').Append(arg is IEnumerable<string> e ? string.Join("|", e) : arg).Append(") =>");
                foreach (var w in kv.Writes.OrderBy(w => w.Key, StringComparer.Ordinal))
                    sb.Append(' ').Append(w.Key).Append('=').Append(w.Value).Append(';');
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    [Test]
    public async Task Setters_PersistTheSameKeysAndFormats()
    {
        string actual = await WriteLog();
        if (Environment.GetEnvironmentVariable("DUMP_SETTINGS_GOLDEN") is { } path)
            await File.WriteAllTextAsync(path, actual);

        await Assert.That(actual).IsEqualTo(AppSettingsGolden.Text);
    }
}
