// RainViewer radar-precipitation tile overlay. Sits between the base
// map (z=0) and chart layers (z=50). Caps at z=12 server-side because
// that's RainViewer's native max; Leaflet upscales above that so the
// nowcast just blurs instead of erroring out from the upstream CDN.
// (OpenWeatherMap would need an API key - not threaded through yet.)
//
// Helm controls opacity via a Misc-section slider (10%..90%). 0.5
// matches the previous baked-in default so an existing install
// without a Settings value keeps the same look.

const WEATHER_OPACITY_DEFAULT = 0.5;

let mapRef = null;
let weatherLayer = null;
let weatherOpacity = WEATHER_OPACITY_DEFAULT;

export function init(map) {
    mapRef = map;
}

export function setWeatherOverlay(tileUrl, opacity) {
    clearWeatherOverlay();
    if (!mapRef || !tileUrl) return;
    if (typeof opacity === 'number' && isFinite(opacity)) {
        weatherOpacity = Math.min(0.95, Math.max(0.05, opacity));
    }
    weatherLayer = L.tileLayer(tileUrl, {
        maxNativeZoom: 12,
        maxZoom: 22,
        opacity: weatherOpacity,
        errorTileUrl: '',
        attribution: '&copy; RainViewer'
    }).addTo(mapRef);
    weatherLayer.setZIndex(40); // Below chart layers (50) but above base map.
}

// Live opacity update without re-fetching tiles. Helm dragging the
// slider gets immediate feedback; cached value persists across the
// next setWeatherOverlay call so a cycle off+on keeps the setting.
export function setWeatherOverlayOpacity(opacity) {
    if (typeof opacity !== 'number' || !isFinite(opacity)) return;
    weatherOpacity = Math.min(0.95, Math.max(0.05, opacity));
    if (weatherLayer) weatherLayer.setOpacity(weatherOpacity);
}

export function clearWeatherOverlay() {
    if (weatherLayer && mapRef) { mapRef.removeLayer(weatherLayer); weatherLayer = null; }
}

export function dispose() {
    weatherLayer = null;
    mapRef = null;
}
