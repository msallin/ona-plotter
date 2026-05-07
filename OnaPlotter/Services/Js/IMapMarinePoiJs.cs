namespace OnaPlotter.Services.Js;

/// <summary>
/// Typed C# wrapper over the leafletInterop.js marine-POI surface.
/// Same shape as <see cref="IMapAisJs"/>: groups the snapshot push +
/// the master visibility toggle so call sites stop re-implementing
/// the JSDisconnected / ObjectDisposed dance.
/// </summary>
public interface IMapMarinePoiJs
{
    /// <summary>
    /// Push the bbox+category-filtered POI snapshot to the JS layer.
    /// Each element is the anonymous-object shape
    /// <c>marinePoiLayer.js#setMarinePois</c> expects:
    /// <c>{ id, category, lat, lon, name, tags }</c>. The JS side
    /// diffs by id and adds / moves / removes markers accordingly.
    /// </summary>
    Task SetMarinePoisAsync(object[] pois);

    /// <summary>Toggle visibility of the layer without dropping the
    /// marker state, so a re-show doesn't have to refetch.</summary>
    Task SetMarinePoisVisibleAsync(bool visible);
}
