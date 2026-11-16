using OnaPlotter.Models;

namespace OnaPlotter.Services.Signalk;

/// <summary>
/// Routes AIS Aids-to-Navigation deltas (context
/// <c>atons.urn:mrn:imo:mmsi:NNN</c>) into <see cref="AtonStore"/>.
/// </summary>
internal sealed class AtonDeltaHandler
{
    private readonly AtonStore _atons;

    public AtonDeltaHandler(AtonStore atons)
    {
        _atons = atons;
    }

    /// <summary>True when the delta context is an AtoN context. An AtoN
    /// context never matches the self context, which is only ever set
    /// from a <c>vessels.*</c> URN.</summary>
    internal static bool IsAtonContext(string? context) =>
        !string.IsNullOrEmpty(context) && context.StartsWith("atons.", StringComparison.Ordinal);

    public void Apply(SignalkDelta delta)
    {
        foreach (var update in delta.Updates!)
        {
            if (update.Values is null) continue;
            foreach (var val in update.Values)
            {
                if (val.Path is null) continue;
                _atons.Apply(delta.Context!, val.Path, val.Value);
            }
        }
    }

    /// <summary>Drops every AtoN. The server re-broadcasts the active set
    /// on reconnect (each AtoN sends Type 21 every ~3 min anyway), so a
    /// stale buoy 100 nm astern is just noise while disconnected.</summary>
    public void Reset() => _atons.Reset();
}
