namespace OnaPlotter.Models;

/// <summary>
/// What the auto-toggle should do for one radar after a status poll.
/// </summary>
public enum RadarOverlayAction
{
    NoOp,
    Enable,
    Disable,
}

/// <summary>
/// Decides whether to enable, disable, or leave the canvas overlay
/// alone for one radar based on its transmit status, the current
/// overlay state, and whether the user has explicitly turned the
/// overlay off this session.
///
/// Intent: the overlay follows transmit by default (so an operator
/// who hits "Transmit" doesn't need a second click to see the data),
/// but a deliberate untick is sticky for the session. The user can
/// re-tick to clear that sticky bit and re-engage auto behavior.
///
/// Pure function so the Map.razor caller stays a thin loop and the
/// rule itself is unit-testable without bUnit / JS interop.
/// </summary>
public static class RadarOverlayAutoToggle
{
    public static RadarOverlayAction Decide(string? status, bool overlayOn, bool userDisabled)
    {
        bool isTransmitting = string.Equals(status, "transmit", System.StringComparison.OrdinalIgnoreCase);
        if (isTransmitting && !overlayOn && !userDisabled) return RadarOverlayAction.Enable;
        // Standby / off / unknown: tear down the canvas; reconnect
        // attempts on a no-data WebSocket are wasted work, and an
        // empty overlay on the chart is a UX trap (looks like a bug).
        if (!isTransmitting && overlayOn) return RadarOverlayAction.Disable;
        return RadarOverlayAction.NoOp;
    }
}
