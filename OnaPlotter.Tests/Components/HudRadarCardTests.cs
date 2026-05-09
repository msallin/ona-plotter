using Bunit;
using OnaPlotter.Components.Map.Hud;
using OnaPlotter.Models;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for the radar HUD card. The component pins three pieces
/// of behaviour the helm relies on:
///   - hidden when no radars are present (no SK plugin == no card,
///     not a permanently-empty card with the user toggle off)
///   - the picker only appears with more than one radar
///   - the range stepper sources its options from the radar's range
///     control validValues (the actual PUT whitelist) and disables
///     the appropriate end button at the boundaries
///   - power buttons fire the right RadarPower value (Stby instant,
///     Transmit via the hold-to-engage path - the latter is exercised
///     in MapHud's autopilot tests; here we just check the click
///     wiring on Stby)
/// </summary>
public class HudRadarCardTests
{
    private static RadarInfo Radar(string id, string status = "transmit",
        int? range = 1852, string? name = "R200") =>
        new() { Id = id, Name = name, Brand = "AcmeRadar", Status = status, Range = range };

    private static IRenderedComponent<HudRadarCard> Render(
        Bunit.TestContext ctx,
        RadarInfo[] radars,
        IReadOnlyDictionary<string, RadarCapabilities?>? caps = null,
        Action<(string, RadarPower)>? onPower = null,
        Action<(string, int)>? onRange = null) =>
        ctx.RenderComponent<HudRadarCard>(p => p
            .Add(x => x.Radars, radars)
            .Add(x => x.Capabilities, caps ?? new Dictionary<string, RadarCapabilities?>())
            .Add(x => x.OnSetPower, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create<(string, RadarPower)>(p, t => onPower?.Invoke(t)))
            .Add(x => x.OnSetRange, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create<(string, int)>(p, t => onRange?.Invoke(t))));

    [Test]
    public async Task Empty_RendersNothing()
    {
        // Setting on but no radars detected -> the card hides itself
        // so the bottom-right HUD stack collapses cleanly.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, []);
        await Assert.That(cut.FindAll(".radar-hud-panel").Count).IsEqualTo(0);
    }

    [Test]
    public async Task SingleRadar_HidesPicker_ShowsName()
    {
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, [Radar("r1")]);
        await Assert.That(cut.FindAll(".radar-hud-picker").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll(".radar-hud-name").Count).IsEqualTo(1);
        await Assert.That(cut.Find(".radar-hud-name").TextContent.Trim()).IsEqualTo("R200");
    }

    [Test]
    public async Task MultipleRadars_ShowsPicker()
    {
        // With more than one radar the dropdown appears so the helm can
        // pick which device the controls act on. Picker omits the
        // single-name fallback row.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, [Radar("r1", name: "R200 24"), Radar("r2", name: "R150 xHD")]);
        await Assert.That(cut.FindAll(".radar-hud-picker").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".radar-hud-name").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll(".radar-hud-picker option").Count).IsEqualTo(2);
    }

    [Test]
    public async Task DefaultsToTransmittingRadar_NotFirstInList()
    {
        // When more than one radar exists and one is transmitting, the
        // card prefers the live one over array order. Reason: the
        // transmitting radar is the one painting on the chart, so the
        // helm's range tweaks should land on that device by default.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx,
            [Radar("idle", status: "standby", name: "Idle"),
             Radar("live", status: "transmit", name: "Live")]);
        // The picker's value attribute reflects the resolved selection.
        await Assert.That(cut.Find(".radar-hud-picker").GetAttribute("value")!).IsEqualTo("live");
    }

    [Test]
    public async Task RangeStepper_Up_AdvancesToNextSupportedValue()
    {
        using var ctx = new Bunit.TestContext();
        (string Id, int M)? captured = null;
        var caps = new Dictionary<string, RadarCapabilities?>
        {
            ["r1"] = new RadarCapabilities
            {
                Controls = new()
                {
                    ["range"] = new ControlDefinition { ValidValues = [463, 926, 1852, 3704] },
                },
            },
        };
        var cut = Render(ctx, [Radar("r1", range: 926)], caps,
            onRange: t => captured = t);

        // Second button is +; should report the next validValue (1852).
        cut.FindAll(".radar-hud-range-step")[1].Click();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Value.Id).IsEqualTo("r1");
        await Assert.That(captured!.Value.M).IsEqualTo(1852);
    }

    [Test]
    public async Task RangeStepper_DisablesAtBoundaries()
    {
        // Bottom of range: the - button is disabled so a tap can't go
        // out of bounds. Top of range: the + button is disabled. Pin
        // both so a refactor of FindIndex that shifts the boundary by
        // one is caught.
        using var ctx = new Bunit.TestContext();
        var caps = new Dictionary<string, RadarCapabilities?>
        {
            ["r1"] = new RadarCapabilities
            {
                Controls = new()
                {
                    ["range"] = new ControlDefinition { ValidValues = [463, 926, 1852] },
                },
            },
        };

        var atMin = Render(ctx, [Radar("r1", range: 463)], caps);
        var minSteps = atMin.FindAll(".radar-hud-range-step");
        await Assert.That(minSteps[0].HasAttribute("disabled")).IsTrue();
        await Assert.That(minSteps[1].HasAttribute("disabled")).IsFalse();

        using var ctx2 = new Bunit.TestContext();
        var atMax = Render(ctx2, [Radar("r1", range: 1852)], caps);
        var maxSteps = atMax.FindAll(".radar-hud-range-step");
        await Assert.That(maxSteps[0].HasAttribute("disabled")).IsFalse();
        await Assert.That(maxSteps[1].HasAttribute("disabled")).IsTrue();
    }

    [Test]
    public async Task RangeStepper_OffListRange_SplicesIntoOptionsAndStepsToNeighbour()
    {
        // When radar.Range isn't in validValues (mid-step from custom
        // firmware, or a stale server-side state), GetRangeOptions
        // splices it into the sorted options. Stepping then moves to
        // the IMMEDIATE neighbour rather than to the first or last
        // valid value - which is the right UX (one tap, one step).
        // Pinning so a future "drop the splice" refactor surfaces
        // the regression: the off-list value would no longer be
        // selectable and StepRange would no-op.
        using var ctx = new Bunit.TestContext();
        (string Id, int M)? captured = null;
        var caps = new Dictionary<string, RadarCapabilities?>
        {
            ["r1"] = new RadarCapabilities
            {
                Controls = new()
                {
                    ["range"] = new ControlDefinition { ValidValues = [463, 926, 1852] },
                },
            },
        };
        // 999 is BETWEEN 926 and 1852 - not in validValues.
        // GetRangeOptions splices it in -> sorted = [463, 926, 999, 1852].
        // Click + steps to 1852 (next neighbour up).
        var cut = Render(ctx, [Radar("r1", range: 999)], caps,
            onRange: t => captured = t);

        cut.FindAll(".radar-hud-range-step")[1].Click();   // + button
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Value.M).IsEqualTo(1852);
    }

    [Test]
    public async Task RangeStepper_OffListRange_StepsDownToImmediateNeighbour()
    {
        // Mirror of the above for the down direction: spliced 999 in
        // [463, 926, 999, 1852] steps DOWN to 926.
        using var ctx = new Bunit.TestContext();
        (string Id, int M)? captured = null;
        var caps = new Dictionary<string, RadarCapabilities?>
        {
            ["r1"] = new RadarCapabilities
            {
                Controls = new()
                {
                    ["range"] = new ControlDefinition { ValidValues = [463, 926, 1852] },
                },
            },
        };
        var cut = Render(ctx, [Radar("r1", range: 999)], caps,
            onRange: t => captured = t);

        cut.FindAll(".radar-hud-range-step")[0].Click();   // - button
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Value.M).IsEqualTo(926);
    }

    [Test]
    public async Task StandbyButton_FiresStandby()
    {
        // Stby is the safety direction; instant tap (no hold). Clicking
        // it must report RadarPower.Standby for the radar whose card
        // is showing, not whatever was first in the list. The power
        // row collapsed to a single state-dependent toggle, so a
        // transmitting radar shows exactly one button - "Stby".
        using var ctx = new Bunit.TestContext();
        (string Id, RadarPower P)? captured = null;
        var cut = Render(ctx, [Radar("r1")], onPower: t => captured = t);

        // Single button in .ap-modes when transmitting - Stby.
        var btns = cut.FindAll(".radar-hud-power .ap-mode-btn");
        await Assert.That(btns.Count).IsEqualTo(1);
        btns[0].Click();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Value.Id).IsEqualTo("r1");
        await Assert.That(captured!.Value.P).IsEqualTo(RadarPower.Standby);
    }

    [Test]
    public async Task TransmitButton_FiresTransmit_OnPlainClick()
    {
        // Helm dropped the 600 ms hold-to-engage gate (the SK radar
        // plugin owns the warmup state machine, so the gate was
        // redundant safety theatre). Both Standby and Transmit are
        // now plain clicks; a tap on the button fires the
        // corresponding RadarPower value.
        using var ctx = new Bunit.TestContext();
        (string Id, RadarPower P)? captured = null;
        var cut = Render(ctx, [Radar("r1", status: "standby")], onPower: t => captured = t);
        var btns = cut.FindAll(".radar-hud-power .ap-mode-btn");
        await Assert.That(btns.Count).IsEqualTo(1);
        btns[0].Click();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Value.Id).IsEqualTo("r1");
        await Assert.That(captured!.Value.P).IsEqualTo(RadarPower.Transmit);
    }

    [Test]
    public async Task PowerToggle_RendersExactlyOneButton()
    {
        // Single-button toggle: one decision surface, label + click
        // behaviour switch by state. Transmitting -> "Standby"
        // (instant tap clicks straight back to standby); standby ->
        // "Transmit" (also instant tap now - helm dropped the
        // hold-to-engage gate). Helm also asked for the full word
        // "Standby" instead of "Stby" so the button reads at a
        // glance without abbreviation guesswork.
        using var ctx = new Bunit.TestContext();
        var transmitting = Render(ctx, [Radar("r1", status: "transmit")]);
        await Assert.That(transmitting.FindAll(".radar-hud-power .ap-mode-btn").Count).IsEqualTo(1);
        await Assert.That(transmitting.Find(".radar-hud-power .ap-mode-btn").TextContent.Trim()).IsEqualTo("Standby");

        using var ctx2 = new Bunit.TestContext();
        var standby = Render(ctx2, [Radar("r2", status: "standby")]);
        await Assert.That(standby.FindAll(".radar-hud-power .ap-mode-btn").Count).IsEqualTo(1);
        await Assert.That(standby.Find(".radar-hud-power .ap-mode-btn").TextContent.Trim()).IsEqualTo("Transmit");
    }

    [Test]
    public async Task StatusChip_ClassIsWhitelisted()
    {
        // Same defence as RadarsSection: a hostile / mid-upgrade status
        // string can't smuggle extra class tokens via the chip. Pin
        // that "transmit hidden" maps to radar-status-unknown, not
        // radar-status-transmit-hidden (which would let CSS class
        // injection visually neutralise the chip).
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, [Radar("r1", status: "transmit hidden")]);
        var chip = cut.Find(".radar-status-chip");
        await Assert.That(chip.GetAttribute("class")!).Contains("radar-status-unknown");
        await Assert.That(chip.GetAttribute("class")!).DoesNotContain("hidden");
    }

    [Test]
    public async Task TransmittingRadar_GetsApEngagedTint()
    {
        // Active-transmit visual cue: the panel borrows the autopilot's
        // .ap-engaged green border so the helm reads "this device is
        // hot" at a glance, matching the AP-engaged colour language.
        using var ctx = new Bunit.TestContext();
        var cut = Render(ctx, [Radar("r1", status: "transmit")]);
        await Assert.That(cut.Find(".radar-hud-panel").ClassList.Contains("ap-engaged")).IsTrue();

        using var ctx2 = new Bunit.TestContext();
        var standby = Render(ctx2, [Radar("r2", status: "standby")]);
        await Assert.That(standby.Find(".radar-hud-panel").ClassList.Contains("ap-engaged")).IsFalse();
    }

    [Test]
    public async Task RangeValue_PrefersDescriptionLabel()
    {
        // The radar's per-value description ("1/4 nm") wins over our
        // metric/nm formatter ("0.2 nm") so the HUD matches the
        // brand-native MFD a helm is muscle-memoried on.
        using var ctx = new Bunit.TestContext();
        var caps = new Dictionary<string, RadarCapabilities?>
        {
            ["r1"] = new RadarCapabilities
            {
                Controls = new()
                {
                    ["range"] = new ControlDefinition
                    {
                        ValidValues = [463, 926, 1852],
                        Descriptions = new()
                        {
                            ["463"] = "1/4 nm",
                            ["926"] = "1/2 nm",
                            ["1852"] = "1 nm",
                        },
                    },
                },
            },
        };
        var cut = Render(ctx, [Radar("r1", range: 463)], caps);
        await Assert.That(cut.Find(".radar-hud-range-value").TextContent.Trim()).IsEqualTo("1/4 nm");
    }
}
