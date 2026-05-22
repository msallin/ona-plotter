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
    /// <summary>Push the current AIS-target snapshot to the map. The
    /// payload is a typed array of <see cref="OnaPlotter.Services.Map.AisVesselPayload"/>
    /// instances pooled by <c>AisPushService</c>; each carries the
    /// pre-resolved shape leafletInterop.js's <c>updateAisTargets</c>
    /// expects (context, lat, lon, COG, SOG, heading, label, palette
    /// key, etc.). The typed shape replaces the previous
    /// <c>object[]</c> of anonymous types so the per-tick allocation
    /// drops from 200 heap objects to one array, and Blazor's interop
    /// serializer can pin the converter once instead of polymorphic-
    /// resolving each element. JS-side reader is unchanged - the wire
    /// keys still serialise camelCase via the explicit
    /// <c>[JsonPropertyName]</c> annotations on the payload.</summary>
    Task UpdateAisTargetsAsync(OnaPlotter.Services.Map.AisVesselPayload[] vessels);

    /// <summary>Push the current AtoN snapshot. The JS side renders
    /// each entry as a static buoy / beacon symbol; static enough not
    /// to need a tick timer.</summary>
    Task SetAtonsAsync(object[] atons);

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

    /// <summary>Push the helm-configured "AIS inactive" threshold (in
    /// minutes) to the JS layer. Used to pin the staleness fade floor
    /// and to suppress the vessel name label for targets past the
    /// threshold so a chart full of ghost MMSIs doesn't drown out the
    /// live targets. Called on startup and whenever the setting
    /// changes.</summary>
    Task SetAisInactiveMinutesAsync(double minutes);

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
