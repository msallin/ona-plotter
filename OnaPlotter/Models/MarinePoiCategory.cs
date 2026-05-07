namespace OnaPlotter.Models;

/// <summary>
/// Sailor-facing category for an OSM marine POI. Drives icon shape /
/// colour in the JS layer and the per-category checkbox set in the
/// Layers panel.
///
/// <para>The category is derived from OSM tags by
/// <see cref="OnaPlotter.Services.Pois.OverpassResponseParser"/>; an
/// element that doesn't match any of these is dropped rather than
/// rendered as "unknown" (an unclassified marker on a chartplotter is
/// noise, not signal).</para>
///
/// <para>When adding a new category:</para>
/// <list type="number">
///   <item><description>Add the enum value here.</description></item>
///   <item><description>Add the Overpass tag query in
///     <see cref="OnaPlotter.Services.Pois.OverpassQueryBuilder"/>.</description></item>
///   <item><description>Add the tag-to-category mapping branch in
///     <see cref="OnaPlotter.Services.Pois.OverpassResponseParser.ClassifyTags"/>.</description></item>
///   <item><description>Add the SVG icon in
///     <c>wwwroot/js/marinePoiLayer.js</c>'s
///     <c>buildMarinePoiSvg</c>.</description></item>
///   <item><description>Add the per-category toggle row in
///     <c>Components/Map/Layers/MarineServicesSection.razor</c>.</description></item>
///   <item><description>Add the persisted-bool field in
///     <see cref="OnaPlotter.Services.Settings.IMarinePoiSettings"/> +
///     <see cref="OnaPlotter.Services.AppSettingsService"/>.</description></item>
/// </list>
/// </summary>
public enum MarinePoiCategory
{
    /// <summary>Marine fuel dock. Tag: <c>amenity=fuel</c> AND
    /// <c>boat=yes</c>. The boat=yes filter is essential -- without
    /// it every road petrol station within 50 km of the coast comes
    /// back. Some marine fuel docks are missing the tag and won't
    /// show; field-tested as the cleaner trade-off.</summary>
    Fuel,

    /// <summary>Marina. Tag: <c>leisure=marina</c>.</summary>
    Marina,

    /// <summary>Commercial / general harbour. Tag:
    /// <c>harbour=yes</c> OR <c>seamark:type=harbour</c>.</summary>
    Harbour,

    /// <summary>Mooring buoy / mooring area. Tag: <c>mooring=yes</c>
    /// OR <c>seamark:type</c> starting with <c>mooring</c>.</summary>
    Mooring,

    /// <summary>Boat ramp / haul-out slipway. Tag:
    /// <c>leisure=slipway</c>.</summary>
    Slipway,

    /// <summary>Pier / jetty. Tag: <c>man_made=pier</c>. Common in
    /// commercial / fishing harbours; also covers tourist piers that
    /// happen to take dinghies.</summary>
    Pier,

    /// <summary>Boat / chandlery shop. Tag: <c>shop=boat</c> OR
    /// <c>shop=ship_chandler</c>.</summary>
    Chandlery,

    /// <summary>Drinking water tap. Tag:
    /// <c>amenity=drinking_water</c>. Without a marine-specific filter,
    /// includes inland park taps; helms accept the false positives in
    /// exchange for finding water on every quay that bothered to tag
    /// the dock tap.</summary>
    DrinkingWater,

    /// <summary>Black-water pump-out station. Tag:
    /// <c>waste_disposal=marine</c> OR <c>pumpout=yes</c>.</summary>
    PumpOut,
}
