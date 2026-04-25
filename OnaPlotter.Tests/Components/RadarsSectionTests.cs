using Bunit;
using OnaPlotter.Components.Map.Layers;
using OnaPlotter.Models;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for RadarsSection. The section is hidden when
/// no radars are present and only the range dropdown's option
/// list and labelling are non-trivial -- everything else is
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
        new() { Id = id, Name = "HALO", Brand = "Navico", Status = "transmit", Range = range };

    private static IRenderedComponent<RadarsSection> RenderExpanded(
        Bunit.TestContext ctx,
        RadarInfo[] radars,
        IReadOnlyDictionary<string, RadarCapabilities?>? caps = null)
    {
        var cut = ctx.RenderComponent<RadarsSection>(p => p
            .Add(x => x.Radars, radars)
            .Add(x => x.Enabled, [])
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
        // Navico HALO ships supportedRanges with metric-rounded and
        // nm-aligned entries (50, 100, 250, ..., 57, 115, 463, ...);
        // but range.validValues is the nm-aligned subset only. PUTting
        // anything outside validValues comes back 'value not legal'.
        // The dropdown must therefore drive off validValues.
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
        // Older / non-Navico providers may not ship validValues; in
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
        // 1234 has no description, falls back to FormatRange ("0.7 nm").
        await Assert.That(labels.Any(l => l.EndsWith("nm") && l != "1/4 nm" && l != "1/2 nm" && l != "1 nm")).IsTrue();
    }
}
