namespace OnaPlotter.Services.Js;

/// <summary>
/// Typed C# wrapper over the leafletInterop.js AIS / AtoN / harbor-mode
/// surface. Groups the per-tick AIS push, the AtoN snapshot push, the
/// own-MMSI seed, the AtoN visibility toggle, and the harbor-mode
/// filter into one feature contract so call sites stop re-implementing
/// the JSDisconnected / ObjectDisposed dance.
/// </summary>
public interface IMapAisJs
{
    /// <summary>Push an AIS update (full snapshot or versioned delta,
    /// see <see cref="OnaPlotter.Services.Map.AisUpdatePayload"/>).
    /// Returns false when the renderer refused it (it doesn't hold the
    /// delta's base, or it deferred the update mid-drag), in which case
    /// the caller must resend a full snapshot.</summary>
    Task<bool> UpdateAisTargetsAsync(OnaPlotter.Services.Map.AisUpdatePayload update);

    /// <summary>Push the current AtoN snapshot. The JS side renders
    /// each entry as a static buoy / beacon symbol; static enough not
    /// to need a tick timer.</summary>
    Task SetAtonsAsync(OnaPlotter.Services.Map.AtonPayload[] atons);

    /// <summary>Toggle visibility of the AtoN layer without dropping
    /// the marker state, so flipping back doesn't have to refetch.</summary>
    Task SetAtonsVisibleAsync(bool visible);

    /// <summary>Toggle AIS vessel name labels (per-target tooltips).
    /// Disabling tears down all existing labels + suppresses creation
    /// on subsequent ticks. Harbor mode independently suppresses
    /// labels; the JS layer treats this flag as the persistent helm
    /// preference and harbor mode as the transient declutter, taking
    /// the AND of both.</summary>
    Task SetAisLabelsVisibleAsync(bool visible);

    /// <summary>Seed the JS side with own-vessel MMSI once the SignalK
    /// hello resolves it. Used by the self-popup HTML to fetch the
    /// country flag from the same endpoint AIS markers do.</summary>
    Task SetOwnMmsiAsync(string mmsi);

    /// <summary>Seed the JS side with own-vessel VHF callsign when
    /// the SignalK feed delivers communication.callsignVhf on self.
    /// Surfaces in the ownship chart popup so the helm can read
    /// MMSI + callsign onto the VHF mic during distress comms
    /// without leaving the chart.</summary>
    Task SetOwnCallsignAsync(string callsign);

    /// <summary>Push harbor-mode flag to JS so name labels, COG vectors,
    /// CPA arcs, and the guard-zone ring vanish in lock-step with the
    /// C# alarm-suppression flip.</summary>
    Task SetHarborModeAsync(bool enabled);

    /// <summary>Pan to the AIS marker for the given vessel context and
    /// open its popup. Returns false when the marker isn't on the map
    /// (vessel aged out, AIS filter hid it, etc.) so the caller can
    /// surface a "vessel no longer on the chart" toast.</summary>
    Task<bool> FocusVesselAsync(string context);

}
