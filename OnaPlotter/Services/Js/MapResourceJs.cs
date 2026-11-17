using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapResourceJs"/> backed by an <see cref="IJSObjectReference"/>
/// handle to the leafletInterop.js module. Centralises the
/// JSDisconnected / ObjectDisposed swallow so call sites don't each
/// re-implement it. After <see cref="MarkDisposed"/> every
/// method becomes a silent no-op.
/// </summary>
public sealed class MapResourceJs : IMapResourceJs
{
    private readonly SafeJsModule _js;

    public MapResourceJs(IJSObjectReference module)
    {
        _js = new SafeJsModule(module);
    }

    public void MarkDisposed() => _js.MarkDisposed();

    public Task AddWaypointMarkerAsync(string id, double? lat, double? lon, string? name,
        string? createdAtIso, bool isMob, bool isActive)
        => _js.InvokeVoidAsync("addWaypointMarker", id, lat, lon, name, createdAtIso, isMob, isActive);

    public Task RemoveWaypointMarkerAsync(string id)
        => _js.InvokeVoidAsync("removeWaypointMarker", id);

    public Task AddNoteMarkerAsync(string id, double lat, double lon, string? title, string? description, string? createdAtIso)
        => _js.InvokeVoidAsync("addNoteMarker", id, lat, lon, title, description, createdAtIso);

    public Task RemoveNoteMarkerAsync(string id)
        => _js.InvokeVoidAsync("removeNoteMarker", id);

    public Task ClearNotesAsync()
        => _js.InvokeVoidAsync("clearNotes");

    public Task OpenNotePopupAsync(string id)
        => _js.InvokeVoidAsync("openNotePopup", id);

    public Task AddRegionAsync(string id, IReadOnlyList<double[][]> rings,
        string? title, string? description, bool isHazard,
        double areaSqM,
        double? centerLat, double? centerLon, double? radiusMeters,
        string? createdAtIso)
        => _js.InvokeVoidAsync("addRegion", id, rings, title, description,
            isHazard, areaSqM, centerLat, centerLon, radiusMeters, createdAtIso);

    public Task RemoveRegionAsync(string id)
        => _js.InvokeVoidAsync("removeRegion", id);

    public Task ClearRegionsAsync()
        => _js.InvokeVoidAsync("clearRegions");

    public Task FocusRegionAsync(string id, double[][] firstRing)
        => _js.InvokeVoidAsync("focusRegion", id, firstRing);

    public Task SetCirclePreviewAsync(double lat, double lon, double radiusMeters)
        => _js.InvokeVoidAsync("setCirclePreview", lat, lon, radiusMeters);

    public Task ClearCirclePreviewAsync()
        => _js.InvokeVoidAsync("clearCirclePreview");
}
