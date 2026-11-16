// RainViewer radar-precipitation tile overlay. Drawn in its own pane
// above the base chart tiles (tilePane, z 200; chart layers can be
// opaque, so anything inside the tile pane underneath them is hidden),
// below the overlay charts (chartOverlayPane, z 260: seamarks stay
// legible through the rain) and below the vector overlays (overlayPane,
// z 400) so routes, AIS vectors and the course line stay on top.
// Semi-transparent, so the base charts show through. Caps at z=12 server-side because
// that's RainViewer's native max; Leaflet upscales above that so the
// nowcast just blurs instead of erroring out from the upstream CDN.
// (OpenWeatherMap would need an API key - not threaded through yet.)
//
// Helm controls opacity via a Misc-section slider (10%..90%). 0.5
// is the default for an install without a Settings value.

const WEATHER_OPACITY_DEFAULT = 0.5;

let mapRef = null;
let weatherLayer = null;
let weatherOpacity = WEATHER_OPACITY_DEFAULT;

const WEATHER_PANE = 'weatherPane';

export function init(map) {
    mapRef = map;
    if (!map.getPane(WEATHER_PANE)) {
        const pane = map.createPane(WEATHER_PANE);
        pane.style.zIndex = '250';
        pane.style.pointerEvents = 'none';
    }
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
        attribution: '&copy; RainViewer',
        pane: WEATHER_PANE,
    }).addTo(mapRef);
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
