using System.Reflection;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Guard against <see cref="FakeSettings"/> falling out of sync with
/// <see cref="IAppSettings"/>. Over the project's lifetime every new
/// user preference (NightModeAuto, ShowAutopilotHud, RadarServerUrl,
/// etc.) added a property + a setter to the interface, and the fix
/// for the resulting test-compile breakage was always "copy the new
/// member into FakeSettings". That works for the first new property;
/// by the third it's a mechanical chore every author forgets once.
///
/// Reflection check: every public instance member on IAppSettings
/// (property getter + Set*Async method) must be realised by
/// FakeSettings. When you add a new member to the interface and see
/// this fail, the fix is one line in FakeSettings -- the error
/// message tells you which.
///
/// Not a substitute for per-feature tests. This only guarantees the
/// fake is wired; each feature using FakeSettings still needs its
/// own assertion that the fake returns a reasonable value.
/// </summary>
public class FakeSettingsParityTests
{
    [Test]
    public async Task FakeSettings_ImplementsEveryMemberOfIAppSettings()
    {
        // Interface surface.
        var iface = typeof(IAppSettings);
        var ifaceProps = iface.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var ifaceMethods = iface.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName)        // skip property getters/setters
            .ToArray();

        // Fake impl surface. Inherits IAppSettings via class declaration.
        var fake = typeof(FakeSettings);
        var fakeProps = fake.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var fakeMethods = fake.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        // Build the missing lists before asserting so the failure
        // message reports ALL gaps at once -- less round-tripping when
        // someone's just added three properties.
        var missingProps = ifaceProps
            .Select(p => p.Name)
            .Where(n => !fakeProps.Contains(n))
            .ToArray();
        var missingMethods = ifaceMethods
            .Select(m => m.Name)
            .Where(n => !fakeMethods.Contains(n))
            .ToArray();

        string msg = "";
        if (missingProps.Length > 0)
            msg += $"FakeSettings is missing properties: {string.Join(", ", missingProps)}. ";
        if (missingMethods.Length > 0)
            msg += $"FakeSettings is missing methods: {string.Join(", ", missingMethods)}. ";

        await Assert.That(missingProps.Length + missingMethods.Length)
            .IsEqualTo(0)
            .Because(msg);
    }

    [Test]
    public async Task FakeSettings_SetterMethods_AreNoThrow()
    {
        // Every Set*Async on the fake should be callable without
        // throwing. The production contract is "persist and notify";
        // the fake's contract is "don't blow up under a test harness".
        // Defensive regression catch: a new Set*Async that throws
        // NotImplementedException by default would break any test
        // that exercises the settings flow after it.
        var fake = new FakeSettings();
        var setters = typeof(IAppSettings).GetMethods()
            .Where(m => m.Name.StartsWith("Set") && m.Name.EndsWith("Async"))
            .ToArray();

        var errors = new List<string>();
        foreach (var m in setters)
        {
            // Fabricate a default for every parameter; most setters are
            // single-arg but SetMapViewAsync takes three (lat, lon, zoom)
            // and any future tuple-style setter should still be testable
            // here without per-method plumbing.
            var parameters = m.GetParameters();
            object?[] args = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                args[i] = DefaultArg(parameters[i].ParameterType);
            try
            {
                var task = (Task)m.Invoke(fake, args)!;
                await task;
            }
            catch (TargetInvocationException ex)
            {
                errors.Add($"{m.Name}: {ex.InnerException?.Message}");
            }
        }
        // One assertion at the end so a single run reports every
        // broken setter, not just the first. Empty error list is the
        // pass state.
        await Assert.That(errors.Count)
            .IsEqualTo(0)
            .Because("FakeSettings setters threw: " + string.Join("; ", errors));
    }

    /// <summary>Fabricates a default argument for each parameter
    /// type the IAppSettings setters accept. Keep this list in sync
    /// as the interface evolves.</summary>
    private static object? DefaultArg(Type t)
    {
        if (t == typeof(bool)) return false;
        if (t == typeof(int)) return 0;
        if (t == typeof(double)) return 0.0;
        if (t == typeof(string)) return "test";
        if (t == typeof(IEnumerable<string>)) return Array.Empty<string>();
        // Fallback: null for any unhandled reference type the interface
        // might gain. The parity test above guarantees the reflection
        // surface stays discoverable, but we don't want this loop to
        // crash on first unknown param shape.
        return null;
    }
}
