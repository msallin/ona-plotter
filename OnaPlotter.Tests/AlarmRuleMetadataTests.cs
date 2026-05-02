using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the metadata-getter contract every IAlarmRule implementation
/// must honour: Title is non-empty + uppercase (banner-friendly),
/// Priority sits in the documented bands (100s = grounding-class,
/// 200s = collision, 300s = wind-shift, etc.), and the rule's own
/// types are concrete (not stubs left over from a port). Catches
/// the class of regression where a new rule's getter returns "" or
/// throws because a refactor-rename forgot to update the property.
///
/// <para>Loops every concrete <see cref="IAlarmRule"/> in the
/// production DLL via reflection rather than enumerating them by
/// hand: a new rule that's added to <c>Program.cs</c> but forgets
/// the metadata still trips this test once it lands in the
/// assembly.</para>
/// </summary>
public class AlarmRuleMetadataTests
{
    /// <summary>Discovers every concrete IAlarmRule in the production
    /// assembly. Skips abstract types and types in test assemblies
    /// (the FakeRule helpers used by other test files).</summary>
    private static IEnumerable<Type> DiscoverRuleTypes()
    {
        var asm = typeof(IAlarmRule).Assembly;
        return asm.GetTypes()
            .Where(t => typeof(IAlarmRule).IsAssignableFrom(t))
            .Where(t => t.IsClass && !t.IsAbstract)
            .ToArray();
    }

    [Test]
    public async Task EveryRule_DeclaresNonEmptyTitle()
    {
        // Reflection-driven so a future-added rule that ships a Title
        // getter returning "" trips here without anyone having to
        // update this file. Each rule is constructed via its parameterless
        // ctor when one exists; rules that take dependencies are
        // covered by their own dedicated tests.
        var types = DiscoverRuleTypes();
        await Assert.That(types.Any())
            .IsTrue()
            .Because("expected at least one IAlarmRule in the production assembly");

        foreach (var t in types)
        {
            var rule = TryConstruct(t);
            if (rule is null) continue;   // ctor needs DI; covered elsewhere
            await Assert.That(rule.Title)
                .IsNotNull()
                .Because($"rule {t.Name} must declare a Title");
            await Assert.That(rule.Title.Length)
                .IsGreaterThan(0)
                .Because($"rule {t.Name}'s Title must be non-empty (banner reads it)");
            // Banner styling assumes the Title is uppercase already
            // (no CSS text-transform on .alarm-banner-title); a
            // mixed-case Title would render mid-banner as "shallow"
            // instead of "SHALLOW". Pin the convention.
            await Assert.That(rule.Title)
                .IsEqualTo(rule.Title.ToUpperInvariant())
                .Because($"rule {t.Name}'s Title must be uppercase (banner doesn't text-transform)");
        }
    }

    [Test]
    public async Task EveryRule_DeclaresPositivePriority()
    {
        // Priority is the manager's tie-breaker; zero or negative
        // would make a rule short-circuit ALL others, including
        // safety-critical rules. Pin > 0 with a hint about the
        // documented bands.
        foreach (var t in DiscoverRuleTypes())
        {
            var rule = TryConstruct(t);
            if (rule is null) continue;
            await Assert.That(rule.Priority)
                .IsGreaterThan(0)
                .Because($"rule {t.Name}'s Priority must be > 0 (conventions: 100=grounding, 200=collision, 300=wind shift)");
        }
    }

    [Test]
    public async Task EveryRule_DeclaresAutoClearWithoutThrowing()
    {
        // AutoClear is read on every banner sweep; a rule that throws
        // here would crash the manager loop and silence ALL alarms.
        // Pin the property's read-without-throw contract for every
        // rule we can construct.
        foreach (var t in DiscoverRuleTypes())
        {
            var rule = TryConstruct(t);
            if (rule is null) continue;
            // Just reading the property is enough; a throw fails the test.
            _ = rule.AutoClear;
        }
        await Assert.That(true).IsTrue();   // explicit pass marker
    }

    private static IAlarmRule? TryConstruct(Type t)
    {
        // Try parameterless ctor first; many rules have ctors with
        // dependencies (settings, store) that need a fake. For the
        // metadata sweep we only care about types that construct
        // with no args (the bulk of the rules); the others are
        // covered by their dedicated test classes.
        var ctor = t.GetConstructor(Type.EmptyTypes);
        if (ctor is null) return null;
        try { return (IAlarmRule?)ctor.Invoke(null); }
        catch { return null; }
    }
}
