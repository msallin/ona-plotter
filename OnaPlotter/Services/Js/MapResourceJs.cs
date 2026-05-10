using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapResourceJs"/> backed by an <see cref="IJSObjectReference"/>
/// handle to the leafletInterop.js module. Centralises the
/// JSDisconnected / ObjectDisposed swallow that every call site
/// previously re-implemented. After <see cref="MarkDisposed"/> every
/// method becomes a silent no-op.
/// </summary>
public sealed class MapResourceJs : IMapResourceJs
{
    private readonly IJSObjectReference _module;
    private bool _disposed;

    public MapResourceJs(IJSObjectReference module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    public void MarkDisposed() => _disposed = true;

    public Task AddWaypointMarkerAsync(string id, double? lat, double? lon, string? name,
        string? createdAtIso, bool isMob, bool isActive)
        => InvokeSafe("addWaypointMarker", id, lat, lon, name, createdAtIso, isMob, isActive);

    public Task RemoveWaypointMarkerAsync(string id)
        => InvokeSafe("removeWaypointMarker", id);

    public Task AddNoteMarkerAsync(string id, double lat, double lon, string? title, string? description, string? createdAtIso)
        => InvokeSafe("addNoteMarker", id, lat, lon, title, description, createdAtIso);

    public Task RemoveNoteMarkerAsync(string id)
        => InvokeSafe("removeNoteMarker", id);

    public Task ClearNotesAsync()
        => InvokeSafe("clearNotes");

    public Task OpenNotePopupAsync(string id)
        => InvokeSafe("openNotePopup", id);

    public Task AddRegionAsync(string id, IReadOnlyList<double[][]> rings,
        string? title, string? description, bool isHazard,
        double areaSqM,
        double? centerLat, double? centerLon, double? radiusMeters,
        string? createdAtIso)
        => InvokeSafe("addRegion", id, rings, title, description,
            isHazard, areaSqM, centerLat, centerLon, radiusMeters, createdAtIso);

    public Task RemoveRegionAsync(string id)
        => InvokeSafe("removeRegion", id);

    public Task ClearRegionsAsync()
        => InvokeSafe("clearRegions");

    public Task FocusRegionAsync(string id, double[][] firstRing)
        => InvokeSafe("focusRegion", id, firstRing);

    public Task SetCirclePreviewAsync(double lat, double lon, double radiusMeters)
        => InvokeSafe("setCirclePreview", lat, lon, radiusMeters);

    public Task ClearCirclePreviewAsync()
        => InvokeSafe("clearCirclePreview");

    private async Task InvokeSafe(string identifier, params object?[] args)
    {
        if (_disposed) return;
        try
        {
            await _module.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException) { /* page is unmounting */ }
        catch (ObjectDisposedException) { /* JS module disposed first */ }
    }
}
