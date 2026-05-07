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
    /// <summary>Push the current AIS-target snapshot to the map. Each
    /// element is the anonymous-object shape leafletInterop.js's
    /// <c>updateAisTargets</c> expects (context, lat, lon, COG, SOG,
    /// heading, label, palette key, etc.); the JS side diffs by id and
    /// adds / moves / removes markers accordingly.</summary>
    Task UpdateAisTargetsAsync(object[] vessels);

    /// <summary>Push the current AtoN snapshot. The JS side renders
    /// each entry as a static buoy / beacon symbol; static enough not
    /// to need a tick timer.</summary>
    Task SetAtonsAsync(object[] atons);

    /// <summary>Toggle visibility of the AtoN layer without dropping
    /// the marker state, so flipping back doesn't have to refetch.</summary>
    Task SetAtonsVisibleAsync(bool visible);

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

    /// <summary>Toggle the on-map guard-zone ring (Misc layers section).
    /// Independent of the CPA alarm pipeline - the alarm still fires
    /// off the radius / lookahead values. Helms can declutter the
    /// chart without disabling the alarm itself.</summary>
    Task SetGuardZoneVisibleAsync(bool visible);

    /// <summary>Toggle the outer dashed warning-band ring at
    /// <c>guardRadius × warningFactor</c>. Helps the helm see why
    /// amber CPA chips appear in the band between the inner danger
    /// ring and the outer warning ring; some helms prefer the
    /// cleaner single-ring look and turn this off. Independent of
    /// the inner ring's visibility.</summary>
    Task SetGuardZoneWarningRingVisibleAsync(bool visible);
}
