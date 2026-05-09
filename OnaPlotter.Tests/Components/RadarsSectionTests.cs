using Bunit;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for RadarsSection. The section is hidden when
/// no radars are present and only the range dropdown's option
/// list and labelling are non-trivial - everything else is
/// pass-through to parent callbacks. These tests pin:
///   - the dropdown sources its values from the range control's
///     validValues when present (not the looser supportedRanges
///     superset that the radar still rejects on PUT)
///   - the dropdown falls back to supportedRanges when the
///     control schema doesn't list validValues
///   - the dropdown shows the radar's own per-value descriptions
///     ("1/4 nm", "1 nm") when available, and the metric/nm
///     fallback otherwise
///   - the radar's current range is always present in the list
///     even when the source list omits it
/// </summary>
public class RadarsSectionTests
{
    private static RadarInfo Radar(string id, int? range = 1852) =>
        new() { Id = id, Name = "R200", Brand = "AcmeRadar", Status = "transmit", Range = range };

    private static IRenderedComponent<RadarsSection> RenderExpanded(
        Bunit.TestContext ctx,
        RadarInfo[] radars,
        IReadOnlyDictionary<string, RadarCapabilities?>? caps = null)
    {
        IReadOnlySet<string> enabled = new HashSet<string>();
        var cut = ctx.RenderComponent<RadarsSection>(p => p
            .Add(x => x.Radars, radars)
            .Add(x => x.Enabled, enabled)
            .Add(x => x.Capabilities, caps ?? new Dictionary<string, RadarCapabilities?>()));
        cut.Find(".section-toggle").Click();
        return cut;
    }

    private static int[] OptionValues(IRenderedComponent<RadarsSection> cut) =>
        cut.FindAll(".radar-range-select option")
           .Select(o => int.Parse(o.GetAttribute("value")!))
           .ToArray();

    private static string[] OptionLabels(IRenderedComponent<RadarsSection> cut) =>
        cut.FindAll(".radar-range-select option")
           .Select(o => o.TextContent)
           .ToArray();

    [Test]
    public async Task Range_Dropdown_Uses_ValidValues_Over_SupportedRanges()
    {
        // Typical recreational radars ship supportedRanges with metric-
        // rounded and nm-aligned entries (50, 100, 250, ..., 57, 115,
        // 463, ...); but range.validValues is the nm-aligned subset
        // only. PUTting anything outside validValues comes back
        // 'value not legal'. The dropdown must therefore drive off
        // validValues.
        using var ctx = new Bunit.TestContext();
        var caps = new Dictionary<string, RadarCapabilities?>
        {
            ["r1"] = new RadarCapabilities
            {
                SupportedRanges = [50, 100, 250, 463, 926, 1852],
                Controls = new()
                {
                    ["range"] = new ControlDefinition { ValidValues = [463, 926, 1852] },
                },
            },
        };
        var cut = RenderExpanded(ctx, [Radar("r1", 1852)], caps);

        await Assert.That(OptionValues(cut)).IsEquivalentTo([463, 926, 1852]);
    }

    [Test]
    public async Task Range_Dropdown_Falls_Back_To_SupportedRanges_When_No_ValidValues()
    {
        // Older / minimal providers may not ship validValues; in
        // that case supportedRanges is the only signal of what the
        // server will accept and we use it as-is.
        using var ctx = new Bunit.TestContext();
        var caps = new Dictionary<string, RadarCapabilities?>
        {
            ["r1"] = new RadarCapabilities
            {
                SupportedRanges = [500, 1000, 2000, 4000],
                // Controls present but range entry has no validValues.
                Controls = new() { ["range"] = new ControlDefinition() },
            },
        };
        var cut = RenderExpanded(ctx, [Radar("r1", 1000)], caps);

        await Assert.That(OptionValues(cut)).IsEquivalentTo([500, 1000, 2000, 4000]);
    }

    [Test]
    public async Task Range_Dropdown_Uses_Default_Ladder_When_Caps_Missing()
    {
        // The placeholder ladder is offered only briefly between
        // /radars arriving and /capabilities completing; an early
        // dropdown click in that window should still see usable
        // options instead of an empty select.
        using var ctx = new Bunit.TestContext();
        var cut = RenderExpanded(ctx, [Radar("r1", 1000)]);

        var values = OptionValues(cut);
        await Assert.That(values).Contains(1000);
        await Assert.That(values).Contains(2000);
        await Assert.That(values.Length).IsGreaterThan(5);
    }

    [Test]
    public async Task Range_Dropdown_Splices_Current_Range_When_Absent_From_Source()
    {
        // The radar's reported live range may not exist in either
        // validValues or supportedRanges (custom firmware, in-flight
        // change, mid-step). The select must still reflect server
        // truth instead of silently snapping to a different option.
        using var ctx = new Bunit.TestContext();
        var caps = new Dictionary<string, RadarCapabilities?>
        {
            ["r1"] = new RadarCapabilities
            {
                Controls = new()
                {
                    ["range"] = new ControlDefinition { ValidValues = [500, 1852] },
                },
            },
        };
        var cut = RenderExpanded(ctx, [Radar("r1", 1234)], caps);

        await Assert.That(OptionValues(cut)).IsEquivalentTo([500, 1234, 1852]);
    }

    [Test]
    public async Task Range_Dropdown_Uses_Description_Labels_When_Available()
    {
        // The per-value descriptions match what the brand's native
        // MFD shows ("1/4 nm" not "0.2 nm"); use them verbatim. Falls
        // back to FormatRange for any value the descriptions table
        // doesn't cover.
        using var ctx = new Bunit.TestContext();
        var caps = new Dictionary<string, RadarCapabilities?>
        {
            ["r1"] = new RadarCapabilities
            {
                Controls = new()
                {
                    ["range"] = new ControlDefinition
                    {
                        ValidValues = [463, 926, 1852, 1234],
                        Descriptions = new()
                        {
                            ["463"] = "1/4 nm",
                            ["926"] = "1/2 nm",
                            ["1852"] = "1 nm",
                            // 1234 absent on purpose
                        },
                    },
                },
            },
        };
        var cut = RenderExpanded(ctx, [Radar("r1", 463)], caps);

        var labels = OptionLabels(cut);
        await Assert.That(labels).Contains("1/4 nm");
        await Assert.That(labels).Contains("1/2 nm");
        await Assert.That(labels).Contains("1 nm");
        // 1234 m has no description: falls back to FormatRange, which
        // formats >=1000 m as `(m/1852.0):F1` nm. Pin the exact value
        // so a regression that returns "0.0 nm" or "1234 m" is caught
        // (the previous predicate-based assertion accepted either).
        await Assert.That(labels).Contains("0.7 nm");
    }

    [Test]
    public async Task Range_Dropdown_Falls_Through_To_SupportedRanges_When_ValidValues_Empty()
    {
        // An older / faulty server could ship validValues: [] instead
        // of omitting the field. The pattern guard `{ Length: > 0 }`
        // forces the fallthrough; pin it so a future refactor that
        // drops the length check would pin the dropdown to an empty
        // list with no signal.
        using var ctx = new Bunit.TestContext();
        var caps = new Dictionary<string, RadarCapabilities?>
        {
            ["r1"] = new RadarCapabilities
            {
                SupportedRanges = [500, 1000, 2000],
                Controls = new()
                {
                    ["range"] = new ControlDefinition { ValidValues = [] },
                },
            },
        };
        var cut = RenderExpanded(ctx, [Radar("r1", 1000)], caps);
        await Assert.That(OptionValues(cut)).IsEquivalentTo([500, 1000, 2000]);
    }

    [Test]
    public async Task Range_Dropdown_Falls_Through_To_Default_When_SupportedRanges_Empty()
    {
        // Both validValues and supportedRanges absent / empty -> the
        // sane default ladder shows. Without this fall-through the
        // dropdown would render as an empty <select>, making the radar
        // unconfigurable in the brief gap before /capabilities lands
        // and on misconfigured providers.
        using var ctx = new Bunit.TestContext();
        var caps = new Dictionary<string, RadarCapabilities?>
        {
            ["r1"] = new RadarCapabilities
            {
                SupportedRanges = [],
                Controls = new(),
            },
        };
        var cut = RenderExpanded(ctx, [Radar("r1", 1000)], caps);
        var values = OptionValues(cut);
        await Assert.That(values.Length).IsGreaterThan(5);
        await Assert.That(values).Contains(1000);
    }

    [Test]
    public async Task Range_Dropdown_Selects_Exactly_The_Current_Range()
    {
        // Blazor renders `selected` on the option whose value matches
        // the select's `value=` attribute - we no longer have to (and
        // shouldn't) emit an explicit `selected=@(v == rangeM)` per
        // option ourselves. Pin that exactly one option ends up
        // selected and it's the radar's current range.
        using var ctx = new Bunit.TestContext();
        var cut = RenderExpanded(ctx, [Radar("r1", 1000)]);
        var selected = cut.FindAll(".radar-range-select option")
            .Where(o => o.HasAttribute("selected"))
            .ToList();
        await Assert.That(selected.Count).IsEqualTo(1);
        await Assert.That(selected[0].GetAttribute("value")!).IsEqualTo("1000");
    }

    [Test]
    public async Task Status_Chip_Class_Is_Whitelisted()
    {
        // Hostile / mid-upgrade Status strings must render as the
        // neutral "unknown" suffix; otherwise a server-controlled
        // string becomes a class-token-injection primitive (e.g.
        // "transmit hidden" would visually disappear the chip).
        // Spec-defined values render with their own suffix.
        using var ctx = new Bunit.TestContext();

        var transmitting = new RadarInfo { Id = "r1", Status = "transmit", Range = 1000 };
        var attacker = new RadarInfo { Id = "r2", Status = "transmit hidden", Range = 1000 };
        var unknown = new RadarInfo { Id = "r3", Status = "weatherMode", Range = 1000 };

        var cut = RenderExpanded(ctx, [transmitting, attacker, unknown]);
        var chips = cut.FindAll(".radar-status-chip");
        await Assert.That(chips.Count).IsEqualTo(3);
        await Assert.That(chips[0].GetAttribute("class")!).Contains("radar-status-transmit");
        await Assert.That(chips[1].GetAttribute("class")!).Contains("radar-status-unknown");
        await Assert.That(chips[1].GetAttribute("class")!).DoesNotContain("hidden");
        await Assert.That(chips[2].GetAttribute("class")!).Contains("radar-status-unknown");
    }
}
