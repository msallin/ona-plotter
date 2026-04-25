using OnaPlotter.Models;

namespace OnaPlotter.Tests.Models;

/// <summary>
/// Unit tests for the auto-toggle decision rule. The rule is the
/// only behaviour Map.razor relies on, so pinning every cell of the
/// (status, overlayOn, userDisabled) truth table here lets the
/// callsite stay a thin loop with no test scaffolding required.
///
/// Behaviour summary the rule implements:
///   - Transmit + overlay-off + not-user-disabled  -> Enable (auto-tick)
///   - Transmit + overlay-off + user-disabled       -> NoOp   (sticky off honoured)
///   - Transmit + overlay-on                        -> NoOp   (already painting)
///   - Standby  + overlay-on                        -> Disable (tear down empty canvas)
///   - Standby  + overlay-off                       -> NoOp   (nothing to do)
///   - Unknown / null status treated as not-transmitting.
/// </summary>
public class RadarOverlayAutoToggleTests
{
    [Test]
    public async Task Transmit_With_Overlay_Off_And_No_User_Override_Enables()
    {
        var action = RadarOverlayAutoToggle.Decide("transmit", overlayOn: false, userDisabled: false);
        await Assert.That(action).IsEqualTo(RadarOverlayAction.Enable);
    }

    [Test]
    public async Task Transmit_With_User_Disabled_Stays_Off()
    {
        // The sticky off is the whole point of the override. After
        // a deliberate untick we don't re-enable on the next poll
        // even though the radar is still transmitting.
        var action = RadarOverlayAutoToggle.Decide("transmit", overlayOn: false, userDisabled: true);
        await Assert.That(action).IsEqualTo(RadarOverlayAction.NoOp);
    }

    [Test]
    public async Task Transmit_With_Overlay_Already_On_Is_NoOp()
    {
        var action = RadarOverlayAutoToggle.Decide("transmit", overlayOn: true, userDisabled: false);
        await Assert.That(action).IsEqualTo(RadarOverlayAction.NoOp);
    }

    [Test]
    public async Task Standby_With_Overlay_On_Disables()
    {
        // Tearing down on standby means the user gets the overlay back
        // on the next transmit cycle without re-ticking, and we don't
        // burn a WebSocket reconnect loop on a no-data stream.
        var action = RadarOverlayAutoToggle.Decide("standby", overlayOn: true, userDisabled: false);
        await Assert.That(action).IsEqualTo(RadarOverlayAction.Disable);
    }

    [Test]
    public async Task Standby_With_Overlay_On_Disables_Even_When_User_Disabled_Is_Set()
    {
        // Defensive: the sticky-off bit only blocks AUTO-ENABLE; if
        // an overlay somehow ended up on while the radar isn't
        // transmitting (race, manual JS call), tear it down anyway.
        var action = RadarOverlayAutoToggle.Decide("standby", overlayOn: true, userDisabled: true);
        await Assert.That(action).IsEqualTo(RadarOverlayAction.Disable);
    }

    [Test]
    public async Task Standby_With_Overlay_Off_Is_NoOp()
    {
        var action = RadarOverlayAutoToggle.Decide("standby", overlayOn: false, userDisabled: false);
        await Assert.That(action).IsEqualTo(RadarOverlayAction.NoOp);
    }

    [Test]
    public async Task Status_Comparison_Is_Case_Insensitive()
    {
        // Spec says "transmit" lowercase but providers have been
        // observed shouting it ("TRANSMIT") and title-casing it.
        await Assert.That(RadarOverlayAutoToggle.Decide("TRANSMIT", false, false))
            .IsEqualTo(RadarOverlayAction.Enable);
        await Assert.That(RadarOverlayAutoToggle.Decide("Transmit", false, false))
            .IsEqualTo(RadarOverlayAction.Enable);
    }

    [Test]
    public async Task Null_Or_Unknown_Status_Treated_As_Not_Transmitting()
    {
        // Missing status (older provider, mid-upgrade) shouldn't
        // start an overlay. If one was somehow on, tear it down.
        await Assert.That(RadarOverlayAutoToggle.Decide(null, false, false))
            .IsEqualTo(RadarOverlayAction.NoOp);
        await Assert.That(RadarOverlayAutoToggle.Decide(null, true, false))
            .IsEqualTo(RadarOverlayAction.Disable);
        await Assert.That(RadarOverlayAutoToggle.Decide("preparing", false, false))
            .IsEqualTo(RadarOverlayAction.NoOp);
        await Assert.That(RadarOverlayAutoToggle.Decide("off", true, false))
            .IsEqualTo(RadarOverlayAction.Disable);
    }

    [Test]
    public async Task Status_With_Surrounding_Whitespace_Is_Not_Transmitting()
    {
        // Spec mandates the literal "transmit" but a server might
        // trim-fail and ship "transmit\n" or " transmit". The compare
        // is OrdinalIgnoreCase WITHOUT trim, so anything but the bare
        // word forces the safe direction (no auto-enable; require an
        // explicit user click). This pins the behaviour so future
        // maintainers see the trim-skip is deliberate.
        await Assert.That(RadarOverlayAutoToggle.Decide("transmit\n", false, false))
            .IsEqualTo(RadarOverlayAction.NoOp);
        await Assert.That(RadarOverlayAutoToggle.Decide(" transmit", false, false))
            .IsEqualTo(RadarOverlayAction.NoOp);
        await Assert.That(RadarOverlayAutoToggle.Decide("transmit ", false, false))
            .IsEqualTo(RadarOverlayAction.NoOp);
    }
}
