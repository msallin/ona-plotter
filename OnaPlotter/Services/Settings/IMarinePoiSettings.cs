namespace OnaPlotter.Services.Settings;

/// <summary>
/// Narrow surface for the OSM marine-POI overlay (Layers > Marine
/// services). Carved from <see cref="OnaPlotter.Services.IAppSettings"/>
/// per the same ISP pattern as <see cref="IMapDisplaySettings"/> -
/// nine category toggles is enough to merit a dedicated slice rather
/// than bloating the map-display interface.
///
/// <para>All toggles default to <c>false</c> so a fresh helm doesn't
/// see Overpass round-trips happening on first chart load. The helm
/// opts in per-category from the Layers panel.</para>
///
/// <para>Each persisted key sits under <c>marinePoi.{category}.v1</c>
/// in the KV store; bump to v2 if the meaning of "enabled" ever
/// changes (e.g. tri-state add / future cluster-marker preference).</para>
/// </summary>
public interface IMarinePoiSettings
{
    /// <summary>Master visibility for the whole marine-services
    /// overlay. When <c>false</c> the controller skips both the
    /// cache-render and the Overpass fetch regardless of the per-
    /// category flags - the helm gets a one-tap "hide everything"
    /// without losing the categories they had ticked on. Defaults
    /// to <c>true</c> so a fresh helm with a category enabled sees
    /// markers immediately. Persisted under
    /// <c>marinePoi.overlayVisible.v1</c>.</summary>
    bool MarinePoiOverlayVisible { get; }

    /// <summary>Marine fuel docks (<c>amenity=fuel</c> +
    /// <c>boat=yes</c>).</summary>
    bool MarinePoiFuelEnabled { get; }

    /// <summary>Marinas (<c>leisure=marina</c>).</summary>
    bool MarinePoiMarinaEnabled { get; }

    /// <summary>Harbours (<c>harbour=yes</c> /
    /// <c>seamark:type=harbour</c>).</summary>
    bool MarinePoiHarbourEnabled { get; }

    /// <summary>Mooring buoys / mooring areas (<c>mooring=yes</c> /
    /// <c>seamark:type=mooring*</c>).</summary>
    bool MarinePoiMooringEnabled { get; }

    /// <summary>Slipways (<c>leisure=slipway</c>).</summary>
    bool MarinePoiSlipwayEnabled { get; }

    /// <summary>Piers / jetties (<c>man_made=pier</c>).</summary>
    bool MarinePoiPierEnabled { get; }

    /// <summary>Boat / chandlery shops (<c>shop=boat</c> /
    /// <c>shop=ship_chandler</c>).</summary>
    bool MarinePoiChandleryEnabled { get; }

    /// <summary>Drinking-water taps (<c>amenity=drinking_water</c>).
    /// May include inland park taps; the helm accepts the false
    /// positives in exchange for finding water on the dock.</summary>
    bool MarinePoiDrinkingWaterEnabled { get; }

    /// <summary>Black-water pump-out stations
    /// (<c>waste_disposal=marine</c> / <c>pumpout=yes</c>).</summary>
    bool MarinePoiPumpOutEnabled { get; }

    Task SetMarinePoiOverlayVisibleAsync(bool value);
    Task SetMarinePoiFuelEnabledAsync(bool value);
    Task SetMarinePoiMarinaEnabledAsync(bool value);
    Task SetMarinePoiHarbourEnabledAsync(bool value);
    Task SetMarinePoiMooringEnabledAsync(bool value);
    Task SetMarinePoiSlipwayEnabledAsync(bool value);
    Task SetMarinePoiPierEnabledAsync(bool value);
    Task SetMarinePoiChandleryEnabledAsync(bool value);
    Task SetMarinePoiDrinkingWaterEnabledAsync(bool value);
    Task SetMarinePoiPumpOutEnabledAsync(bool value);
}
