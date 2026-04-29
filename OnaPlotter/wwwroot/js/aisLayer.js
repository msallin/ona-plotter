// AIS / radar / SART target rendering. Owns:
//   * Per-vessel marker (chevron / radar triangle / SART pulsing
//     bullseye), name label, COG vector, and rolling 60 s trail.
//   * CPA crossing-situation overlay: lines from each vessel to its
//     predicted CPA point + a labelled chip at the midpoint.
//   * Guard zone ring around own boat (CPA alarm radius).
//   * Harbor mode declutter: drops labels / vectors / CPA overlays
//     and hides the guard ring without losing per-context state.
// Visual fields (ship-type palette, glyph category, SART category,
// CPA threat band) are resolved on the C# side; JS just draws them.

import { DEG, NM_PER_METER, haversineMeters, bearingDeg, destPoint, vectorEnd } from './geoMath.js';

// HTML-escape untrusted strings for popup content.
function esc(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }

let mapRef = null;
let colors = null;
let isSlowClient = false;
let getDotNetRef = null;
let getEditModeFlags = null;       // () => { routeEdit, polygonEdit, measure }
let editModeAddPoint = null;       // (mode, lat, lon) -> void
let getOwnMmsi = null;
let flagUrl = null;
let rotateMarker = null;

// Per-vessel rendered state. Keys are SignalK contexts.
const aisMarkers = {};
const aisVectors = {};
const aisCpaOwnLines = {};
const aisCpaTgtLines = {};
const aisCpaLabels = {};
const aisTrailHistory = {};
const aisTrailLines = {};
const aisLabels = {};
const AIS_TRAIL_SECONDS = 60;

// Best-effort external-lookup cache for vessels whose SignalK feed
// hasn't yet delivered a static-data AIS message (message 5 / 24).
// Keyed by MMSI. A value of null means "looked up and came back
// empty" -- prevents endless retries. Bounded: on insert past
// VESSEL_NAME_CACHE_MAX we drop the oldest entry. A Map is used
// because its iteration is insertion-ordered, so the first key is
// the oldest, which is all we need for a simple LRU with promote-
// on-hit.
const VESSEL_NAME_CACHE_MAX = 500;
const vesselNameCache = new Map();

// Set of MMSIs we've already kicked a flag-image fetch for. The
// flag lives behind /signalk/v2/api/resources/flags/mmsi/{mmsi} via
// the signalk-flags plugin; lazy-loading only on popup-open gave a
// visible flash as the user scrolled through AIS targets in a busy
// harbour. Pre-warming on the first updateAisTargets tick lets the
// browser cache handle subsequent opens.
const flagsPrewarmed = new Set();

// Icon caches (one per source x colour x category combo).
const aisIconCache = {};
const radarIconCache = {};
const sartIconCache = {};
// AIS icon size. 28 leaves own boat (30) visibly bigger while making
// other traffic actually legible at chart zoom. 24 read as "too
// small" on a helm screen, especially with a ship-type glyph
// overlaid -- the glyph shrank to noise.
const AIS_ICON_SIZE = 28;
const RADAR_ICON_SIZE = 26;

// Own vessel state, pushed by the mux on every updatePosition tick.
let selfLat = 0, selfLon = 0, selfCogRad = null, selfSogMs = null;

// Guard zone (CPA alarm envelope drawn around own boat).
let guardZoneRing = null;
let guardZoneRadiusNm = 0.5;       // default matches IAppSettings.CpaAlarmThreshold
let guardZoneLookaheadMin = 10;    // default matches IAppSettings.GuardZoneLookaheadMinutes
let guardZoneWarningFactor = 2.0;  // default matches IAppSettings.GuardZoneWarningFactor

// Harbor-mode flag. When true the AIS render path skips name labels,
// COG vectors, and CPA overlays, the guard-zone ring is not drawn,
// and moored vessels are filtered upstream in C#.
let harborMode = false;

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
    isSlowClient = deps.isSlowClient;
    getDotNetRef = deps.getDotNetRef;
    getEditModeFlags = deps.getEditModeFlags;
    editModeAddPoint = deps.editModeAddPoint;
    getOwnMmsi = deps.getOwnMmsi;
    flagUrl = deps.flagUrl;
    rotateMarker = deps.rotateMarker;
}

// Mux pushes the own-boat snapshot. CPA prediction needs SOG / COG
// in addition to lat / lon, and the guard-zone ring chases the boat.
export function setBoatPosition(lat, lon, cogRad, sogMs) {
    selfLat = lat;
    selfLon = lon;
    selfCogRad = cogRad;
    selfSogMs = sogMs;
    if (guardZoneRing) guardZoneRing.setLatLng([lat, lon]);
}

// --- icons ---

function makeBoatSvg(fill, size, isOwn, category) {
    const s = size || 28;
    const h = s / 2;
    // Sleek arrow shape: pointed bow, tapered stern with notch.
    const outline = isOwn
        ? `stroke="#fff" stroke-width="1.2" stroke-linejoin="round"`
        : `stroke="rgba(255,255,255,0.5)" stroke-width="0.8" stroke-linejoin="round"`;
    return `<svg width="${s}" height="${s}" viewBox="-${h} -${h} ${s} ${s}" style="filter:drop-shadow(0 1px 2px rgba(0,0,0,0.4))">
              <polygon points="0,-${h-2} ${h-6},${h-4} 0,${h-8} -${h-6},${h-4}" fill="${fill}" ${outline} opacity="${isOwn ? 1 : 0.9}"/>
              ${shipTypeGlyph(category)}
            </svg>`;
}

// Small glyph overlaid near the centre of the chevron. Kept to 3-4 px
// so it never obscures the outline; stroke colour is a fixed dark so
// it reads on any coloured chevron (including muted warms). The
// category string comes from C# (Utilities/AisPalette.ShipTypeCategory);
// this function is a pure renderer that maps a category to SVG.
function shipTypeGlyph(category) {
    const stroke = `stroke="rgba(0,0,0,0.7)" stroke-width="0.9" stroke-linecap="round"`;
    switch (category) {
        case 'sail':       // small diamond
            return `<polygon points="0,-3 2.5,0 0,3 -2.5,0" fill="rgba(255,255,255,0.85)" ${stroke}/>`;
        case 'fish':       // crossed nets
            return `<line x1="-2.5" y1="-2" x2="2.5" y2="2" ${stroke}/>`
                 + `<line x1="-2.5" y1="2"  x2="2.5" y2="-2" ${stroke}/>`;
        case 'commercial': // solid dot (bulk / deck superstructure)
            return `<circle cx="0" cy="0.5" r="1.8" fill="rgba(0,0,0,0.75)"/>`;
        case 'service':    // plus
            return `<line x1="-2.5" y1="0" x2="2.5" y2="0" ${stroke}/>`
                 + `<line x1="0" y1="-2.5" x2="0" y2="2.5" ${stroke}/>`;
        default:
            return '';
    }
}

function makeIcon(html, size) {
    return L.divIcon({ className: 'boat-icon', html, iconSize: [size, size], iconAnchor: [size/2, size/2] });
}

function makeRadarSvg(fill, size) {
    const s = size || 22;
    const h = s / 2;
    return `<svg width="${s}" height="${s}" viewBox="-${h} -${h} ${s} ${s}" `
         + `style="filter:drop-shadow(0 1px 2px rgba(0,0,0,0.4))">`
         + `<polygon points="0,-${h-3} ${h-4},${h-5} -${h-4},${h-5}" `
         + `fill="none" stroke="${fill}" stroke-width="1.6" stroke-linejoin="round" opacity="0.95"/>`
         + `<circle cx="0" cy="0" r="1.6" fill="${fill}"/></svg>`;
}

function getAisIcon(color, category) {
    const key = `${color}|${category || ''}`;
    if (!aisIconCache[key]) {
        aisIconCache[key] = makeIcon(
            makeBoatSvg(color, AIS_ICON_SIZE, false, category), AIS_ICON_SIZE);
    }
    return aisIconCache[key];
}
function getRadarIcon(color) {
    if (!radarIconCache[color]) {
        radarIconCache[color] = makeIcon(
            makeRadarSvg(color, RADAR_ICON_SIZE), RADAR_ICON_SIZE);
    }
    return radarIconCache[color];
}

// SART / MOB / EPIRB marker: large pulsing red bullseye with the
// category label inside. Intentionally nothing like the chevron so
// it reads as "not a vessel, emergency transmitter" at a glance.
// The CSS .sart-icon class drives the pulse animation.
function getSartIcon(category) {
    if (!sartIconCache[category]) {
        const label = category;
        const size = 44;
        const html = `
            <div class="sart-icon sart-${label.toLowerCase()}">
                <svg width="${size}" height="${size}" viewBox="-22 -22 44 44">
                    <circle cx="0" cy="0" r="18" fill="rgba(239,68,68,0.18)"
                            stroke="#ef4444" stroke-width="2"/>
                    <circle cx="0" cy="0" r="10" fill="rgba(239,68,68,0.35)"
                            stroke="#ef4444" stroke-width="1.2"/>
                    <text x="0" y="3" fill="#fff" font-size="7" font-weight="700"
                          text-anchor="middle" style="letter-spacing:0.05em;"
                          paint-order="stroke" stroke="#7f1d1d" stroke-width="0.6">${label}</text>
                </svg>
            </div>`;
        sartIconCache[category] = L.divIcon({
            className: 'sart-icon-wrapper',
            html,
            iconSize: [size, size],
            iconAnchor: [size / 2, size / 2],
        });
    }
    return sartIconCache[category];
}

function prewarmFlag(mmsi) {
    if (!mmsi || flagsPrewarmed.has(mmsi)) return;
    flagsPrewarmed.add(mmsi);
    const img = new Image();
    // Image() doesn't block, no onerror noise (plugin-missing fetches
    // are absorbed silently since no element is attached to the DOM).
    img.src = flagUrl(mmsi);
}

// --- vessel name cache ---

function vesselNameCacheGet(mmsi) {
    if (!vesselNameCache.has(mmsi)) return undefined;
    const v = vesselNameCache.get(mmsi);
    // Promote: re-insert at the end so a recently-used entry isn't next to evict.
    vesselNameCache.delete(mmsi);
    vesselNameCache.set(mmsi, v);
    return v;
}

function vesselNameCacheSet(mmsi, name) {
    if (vesselNameCache.has(mmsi)) vesselNameCache.delete(mmsi);
    vesselNameCache.set(mmsi, name);
    while (vesselNameCache.size > VESSEL_NAME_CACHE_MAX) {
        const oldest = vesselNameCache.keys().next().value;
        vesselNameCache.delete(oldest);
    }
}

function vesselNameCacheHas(mmsi) { return vesselNameCache.has(mmsi); }

// Vessel-name enrichment is disabled. The previous implementation
// used api.allorigins.win as a CORS proxy to scrape vesselfinder.com,
// but that proxy itself stopped sending CORS headers and now floods
// the console. The SignalK server already receives AIS message
// type 5 (static data) for named vessels within minutes of first
// sighting, so the common scenario is "wait a bit and the name
// shows up". A proper long-term home for this lookup is a SignalK
// server-side plugin.
//
// The function is kept as a no-op so call sites remain; the
// per-MMSI cache is still honored for any externally injected
// values.
async function resolveVesselName(_context, _mmsi) {
    return null;
}

// --- popup builder ---

/**
 * Builds the full AIS popup HTML string from a vessel snapshot.
 * Called lazily -- only when the popup is actually about to open or
 * is already open and the data changed. Building 200+ of these
 * every 3 s when the user isn't looking at any of them was visible
 * perf overhead on a weak client.
 */
function buildAisPopupHtml(snap) {
    const { v, selfLat: _selfLat, selfLon: _selfLon, cpaInfo, isDangerEff, isWarning } = snap;
    const name = esc(v.name || '');
    const mmsi = v.mmsi || '';
    const callsign = v.callsign ? esc(v.callsign) : '';
    const sog = v.sogMs != null ? (v.sogMs * 1.94384).toFixed(1) : '--';
    const cogDeg = v.cogRad != null ? (v.cogRad * DEG).toFixed(0) : '--';
    const hdgDeg = v.headingRad != null ? (v.headingRad * DEG).toFixed(0) : '--';
    const type = v.shipType ? esc(v.shipType) : '';
    const dist = haversineMeters(_selfLat, _selfLon, v.lat, v.lon) * NM_PER_METER;
    const brg = bearingDeg(_selfLat, _selfLon, v.lat, v.lon);

    // Display name preference: SignalK name -> external-lookup cache
    // -> callsign -> MMSI.
    let displayTitle;
    const cachedName = mmsi ? vesselNameCacheGet(mmsi) : undefined;
    if (name)            displayTitle = name;
    else if (cachedName) displayTitle = esc(cachedName);
    else if (callsign)   displayTitle = callsign;
    else if (mmsi)       displayTitle = `MMSI ${esc(mmsi)}`;
    else                 displayTitle = 'Unknown';
    if (v.buddy) displayTitle = '★ ' + displayTitle;

    let cpaHtml = '';
    if (cpaInfo && cpaInfo.tcpa > 0) {
        const cls = isDangerEff ? 'color:#f87171;font-weight:600' : 'opacity:0.8';
        cpaHtml = `<tr><td style="opacity:0.5">CPA</td><td style="${cls}">${cpaInfo.cpa.toFixed(2)} nm in ${cpaInfo.tcpa.toFixed(0)} min</td></tr>`;
    }

    let colregsHtml = '';
    if (v.colregsLabel) {
        const roleHtml = v.colregsRole
            ? ` <span style="color:${v.colregsRole === 'Give way' ? '#fca5a5' : '#86efac'};font-weight:600">${esc(v.colregsRole)}</span>`
            : '';
        colregsHtml = `<tr><td style="opacity:0.5">COLREGS</td><td>${esc(v.colregsLabel)}${roleHtml}</td></tr>`;
    }

    // External lookup links (free, no API key needed). VesselFinder's
    // search page uses ?name= even for MMSI queries.
    const mtUrl = mmsi ? `https://www.marinetraffic.com/en/ais/details/ships/mmsi:${esc(mmsi)}` : '';
    const vfUrl = mmsi ? `https://www.vesselfinder.com/vessels?name=${esc(mmsi)}` : '';

    // Buddy toggle + per-vessel snooze. Inline data attributes so the
    // delegated handler on mapEl can route both to Blazor without
    // leaking a callback through string concatenation.
    const buddyLabel = v.buddy ? '★ Remove buddy' : '☆ Add buddy';
    const buddyAttrs = `data-ona-buddy="1" data-ctx="${esc(v.context)}" data-mmsi="${esc(mmsi || '')}"`
        + ` data-nm="${esc(v.name || '')}" data-is="${v.buddy ? '1' : '0'}"`;
    const showSnooze = !v.buddy && (isDangerEff || isWarning);
    const snoozeAttrs = `data-ona-snooze="1" data-ctx="${esc(v.context)}" data-nm="${esc(v.name || mmsi || '')}"`;
    const snoozeHtml = showSnooze
        ? `<a href="#" ${snoozeAttrs} style="color:#fbbf24;font-size:11px;text-decoration:none">♫ Snooze alarm</a>`
        : '';

    // Two action rows. Row 1: external-lookup links (MarineTraffic +
    // VesselFinder) side-by-side on a single flex line -- they're
    // the primary "tell me more about this vessel" action. Row 2:
    // local actions (Buddy toggle + Snooze alarm) since they mutate
    // app state and belong together. Splitting the rows stops the
    // local actions from wrapping between the two external links on
    // narrow popups and groups them by intent.
    let linksHtml = '';
    if (mmsi || showSnooze) {
        const linkStyle = 'color:#7dd3fc;font-size:11px;text-decoration:none;flex:1;text-align:center;padding:2px 4px;white-space:nowrap';
        const rows = [];
        if (mmsi) {
            rows.push(
                `<div style="display:flex;gap:10px;align-items:center">` +
                `<a href="${mtUrl}" target="_blank" rel="noopener" style="${linkStyle}">MarineTraffic</a>` +
                `<a href="${vfUrl}" target="_blank" rel="noopener" style="${linkStyle}">VesselFinder</a>` +
                `</div>`
            );
        }
        const row2 = [];
        if (mmsi) {
            row2.push(`<a href="#" ${buddyAttrs} style="color:#facc15;font-size:11px;text-decoration:none">${buddyLabel}</a>`);
        }
        if (snoozeHtml) row2.push(snoozeHtml);
        if (row2.length > 0) {
            rows.push(`<div style="display:flex;gap:12px;flex-wrap:wrap">${row2.join('')}</div>`);
        }
        linksHtml = `<div style="margin-top:6px;padding-top:6px;border-top:1px solid rgba(255,255,255,0.08);display:flex;flex-direction:column;gap:6px">` +
            rows.join('') + `</div>`;
    }

    // Country flag from signalk-flags plugin. 404s on servers without
    // the plugin trigger onerror + hide; no broken-image glyph.
    const flagHtml = mmsi
        ? `<img class="ais-popup-flag" src="${flagUrl(mmsi)}" alt="" onerror="this.style.display='none'">`
        : '';

    return (
        `<div class="ais-popup-content">` +
        `<div class="ais-popup-title">${flagHtml}${displayTitle}</div>` +
        (type ? `<div class="ais-popup-type">${type}</div>` : '') +
        `<table class="ais-popup-table">` +
          (mmsi ? `<tr><td>MMSI</td><td>${esc(mmsi)}</td></tr>` : '') +
          (callsign ? `<tr><td>Call</td><td>${callsign}</td></tr>` : '') +
          `<tr><td>SOG</td><td>${sog} kn</td></tr>` +
          `<tr><td>COG</td><td>${cogDeg}&deg;</td></tr>` +
          `<tr><td>HDG</td><td>${hdgDeg}&deg;</td></tr>` +
          `<tr><td>Dist</td><td>${dist.toFixed(2)} nm</td></tr>` +
          `<tr><td>BRG</td><td>${brg.toFixed(0)}&deg;</td></tr>` +
          cpaHtml +
          colregsHtml +
        `</table>` +
        linksHtml +
        `</div>`
    );
}

// --- main update ---

export function updateAisTargets(vessels) {
    if (!mapRef) return;
    // During an active drag a full AIS rebuild is the largest per-frame
    // cost in the module (200+ markers, CPA overlays, trails, popups).
    // Defer until the user lets go; the next 3 s tick picks up any
    // changes that happened during the drag. Safety is preserved
    // because alarms run on the C# side off the delta stream, not
    // off the JS marker state.
    if (isSlowClient && mapRef.dragging && mapRef.dragging._moving) return;
    const seen = new Set();

    for (const v of vessels) {
        seen.add(v.context);
        if (v.lat == null || v.lon == null || !isFinite(v.lat) || !isFinite(v.lon)) continue;

        // Pre-fetch the country flag on first sight so the AIS popup
        // doesn't flash while it loads the SVG on first click.
        if (v.mmsi) prewarmFlag(v.mmsi);

        // CPA + TCPA come pre-computed from the C# side (Utilities/Cpa)
        // so the map marker path and the Layers-panel list can't disagree.
        // Shape the expected record for the rest of the loop.
        const cpaInfo = (v.cpaNm != null && v.tcpaMin != null)
            ? { cpa: v.cpaNm, tcpa: v.tcpaMin }
            : null;
        // CPA threat band is also computed C#-side (Cpa.ClassifyThreat)
        // using the helm's guard-zone radius / lookahead / warning factor.
        // Three buckets: "danger" (red ring + red crossing line),
        // "warning" (amber crossing line, advisory), "none" (no overlay).
        // Buddies are exempted on the C# side so we don't re-check here.
        const isDangerEff = v.cpaThreat === 'danger';
        const isWarning   = v.cpaThreat === 'warning';
        // Visual fields are resolved on the C# side (AisPalette /
        // AisSart) and arrive on the vessel payload:
        //   v.sartCategory  - "SART"/"MOB"/"EPIRB" or null
        //   v.glyphCategory - "sail"/"fish"/"commercial"/"service" or null
        //   v.shipColor     - hex string from the ship-type palette
        // JS only applies the runtime overrides (danger / buddy) since
        // those are derived from CPA state that's computed here in JS.
        const isSart = v.sartCategory != null;

        // Radar targets use their own outline-triangle icon in a fixed tan
        // tone; AIS targets fall back to the C#-resolved ship-type colour.
        const isRadar = v.source === 'radar';
        let color;
        if (isRadar) {
            color = isDangerEff ? colors.danger : colors.radar;
        } else if (v.buddy) {
            color = colors.buddy;              // buddies always win
        } else if (isDangerEff) {
            color = colors.danger;             // CPA alarm active
        } else {
            color = v.shipColor || '#e0c9a6';     // palette default from C#
        }
        const category = isRadar ? null : v.glyphCategory;
        const icon = isSart
            ? getSartIcon(v.sartCategory)
            : (isRadar ? getRadarIcon(color) : getAisIcon(color, category));

        let marker = aisMarkers[v.context];
        if (!marker) {
            marker = L.marker([v.lat, v.lon], { icon }).addTo(mapRef);
            aisMarkers[v.context] = marker;
            // During route / polygon / measurement edit, a tap on a vessel
            // should behave like a tap on empty water: append a waypoint,
            // not open the vessel popup. Without this guard the click
            // reaches the marker first (Leaflet's default binding), the
            // popup shows, and the route never picks up the point. We
            // attach the guard on FIRST CREATE so the once-per-marker
            // cost is trivial even in 200-vessel harbours.
            marker.on('click', (ev) => {
                const flags = getEditModeFlags();
                if (flags.routeEdit || flags.polygonEdit || flags.measure) {
                    L.DomEvent.stopPropagation(ev);
                    L.DomEvent.preventDefault(ev);
                    const ll = ev.latlng || marker.getLatLng();
                    if (flags.routeEdit)         editModeAddPoint('route', ll.lat, ll.lng);
                    else if (flags.polygonEdit)  editModeAddPoint('polygon', ll.lat, ll.lng);
                    else                         editModeAddPoint('measure', ll.lat, ll.lng);
                    marker.closePopup();
                }
            });
        } else {
            marker.setLatLng([v.lat, v.lon]);
            marker.setIcon(icon);
        }
        if (!isSart) rotateMarker(marker, v.cogRad ?? v.headingRad);

        // Pulse an expanding red ring around any AIS / radar target
        // whose CPA is in the "danger" band (matches the colors.danger
        // tint on the chevron). Adds a .cpa-pulse class to the marker
        // element, which the CSS drives via ::after. SART gets its own
        // pulse so we skip it here to avoid double-pulsing.
        if (!isSart) {
            const el = marker.getElement();
            if (el) el.classList.toggle('cpa-pulse', isDangerEff);
        }

        // Vessel staleness. Anything not heard from in >30 s is
        // geometrically stale -- its rendered position is a guess,
        // not a fix. Fade the marker + trail so the helm's eye lands
        // on live targets first. SART pulses regardless (life-safety
        // beacons can drop out briefly and still matter); buddies
        // also keep full opacity because the "where's my friend"
        // workflow tolerates lateness. When a previously-faded target
        // flips to SART/buddy status mid-session we MUST clear the
        // opacity style we wrote earlier, otherwise it stays dim.
        {
            const el = marker.getElement();
            if (el) {
                if (isSart || v.buddy) {
                    if (el.style.opacity !== '') el.style.opacity = '';
                } else {
                    const ageSec = v.ageSec ?? 0;
                    let op;
                    if (ageSec >= 300)      op = '0.25';     // >5 min
                    else if (ageSec >= 30)  op = (1 - 0.65 * (ageSec - 30) / 270).toFixed(2);
                    else                    op = '';         // fresh
                    // Only write when the bucket actually changes; 200+
                    // vessels in a harbour re-writing style every tick
                    // invalidates layout for nothing.
                    if (el.style.opacity !== op) el.style.opacity = op;
                }
            }
        }

        // Name label visible at zoom >= 12. Resolution (name -> mmsi,
        // with buddy star prefix) happens C#-side -- Map.razor.PushAisTargets
        // stamps v.displayName so this label and any other label-rendering
        // surface share one fallback chain. Suppressed in harbor mode
        // to keep the chart legible when entering a busy port.
        const displayName = v.displayName || null;
        if (displayName && !harborMode) {
            if (!aisLabels[v.context]) {
                aisLabels[v.context] = L.tooltip({
                    permanent: true, direction: 'right', offset: [12, 0],
                    className: 'ais-label'
                });
                marker.bindTooltip(aisLabels[v.context]);
            }
            aisLabels[v.context].setContent(esc(displayName));
        }

        // Rich popup with vessel details and external lookup links.
        // Building the HTML for every vessel every tick (200+ in a busy
        // harbour, 3 s cadence) shows up in profiles as measurable
        // overhead even though most popups are never opened. We now
        // STASH the snapshot on the marker and only rebuild when the
        // popup is actually visible -- once on popupopen and again on
        // each tick the popup stays open. buildAisPopupHtml reads the
        // stashed data directly so the per-vessel HTML work is deferred
        // to the lazy path.
        marker._onaVesselSnapshot = {
            v, selfLat, selfLon, cpaInfo,
            isDangerEff, isWarning,
        };

        if (!marker.getPopup()) {
            // First bind: placeholder content + popupopen listener that
            // rebuilds the real HTML from the stashed snapshot before
            // showing.
            // maxWidth 420 (was 280): the popup contains ~8 label/value
            // rows plus an action row with MarineTraffic / VesselFinder /
            // Buddy / Snooze links. At 280 px the action row wrapped onto
            // three lines on iPad landscape and the MT / VF links split
            // across rows; 420 keeps them on one line and reads cleaner.
            // autoPan: false -- a CPA banner often prompts the helm to
            // tap the threatening AIS marker to investigate, and the
            // default Leaflet popup auto-pan would shift the map away
            // from own boat to fit the popup. The helm wanted "no
            // focus change on collision course" -- they want to see
            // own boat AND the threat geometry, not have the chart
            // jerk to keep a popup on screen. Helm can still pan
            // manually.
            marker.bindPopup('',
                { closeButton: false, maxWidth: 420, className: 'ais-popup', autoPan: false });
            marker.on('popupopen', () => {
                if (marker._onaVesselSnapshot) {
                    marker.setPopupContent(buildAisPopupHtml(marker._onaVesselSnapshot));
                }
            });
        } else if (marker.isPopupOpen()) {
            // Popup is on screen right now -- user is watching. Refresh
            // live so the SOG / CPA / buddy toggle label update without
            // a close-reopen round-trip.
            marker.setPopupContent(buildAisPopupHtml(marker._onaVesselSnapshot));
        }

        // External name lookup kick-off stays on the fast path: the
        // result affects the on-chart label tooltip (always visible at
        // zoom >= 12), not just the popup, so we don't want to gate it
        // on popup-open.
        if (!v.name && v.mmsi && !vesselNameCacheHas(v.mmsi)) {
            resolveVesselName(v.context, v.mmsi);
        }

        // Trail: last AIS_TRAIL_SECONDS of positions, drawn as a fading line.
        // We only push when the position actually changes to avoid empty ticks.
        updateAisTrail(v.context, v.lat, v.lon);

        // Course vector. Suppressed in harbor mode -- with dozens of
        // AIS targets in port every vector sweeps across every other
        // marker and the chart turns into a hatch of dashed lines.
        const end = vectorEnd(v.lat, v.lon, v.cogRad, v.sogMs);
        if (end && !harborMode) {
            let vec = aisVectors[v.context];
            if (!vec) {
                vec = L.polyline([[v.lat, v.lon], end], {
                    color, weight: 1.5, dashArray: '6,4'
                }).addTo(mapRef);
                aisVectors[v.context] = vec;
            } else {
                vec.setLatLngs([[v.lat, v.lon], end]);
                vec.setStyle({ color });
            }
        }

        // Crossing-situation lines: draw from each vessel's current position
        // to its predicted CPA point, plus a label with CPA / TCPA at the
        // target's CPA dot. Rendered for danger (red) and warning (yellow).
        // Buddies never render these - they're exempt from the alarm pipeline
        // and the red lines would be misleading. Suppressed in harbor
        // mode where every other vessel is technically a "near miss" --
        // the audio CPA alarm is suppressed in C# (CpaAlarmRule short-
        // circuits on Settings.HarborMode) and the on-chart overlay
        // would just add noise to a chart the helm needs to read.
        if ((isDangerEff || isWarning) && cpaInfo && cpaInfo.tcpa > 0 && !harborMode) {
            const tcpaSec = cpaInfo.tcpa * 60;
            const ownCpa = destPoint(selfLat, selfLon, selfCogRad, selfSogMs * tcpaSec);
            const tgtCpa = destPoint(v.lat, v.lon, v.cogRad, v.sogMs * tcpaSec);
            const lineColor = isDangerEff ? colors.mob : colors.guardWarn;

            updateCpaLine(aisCpaOwnLines, v.context, [selfLat, selfLon], ownCpa, lineColor);
            updateCpaLine(aisCpaTgtLines, v.context, [v.lat, v.lon], tgtCpa, lineColor);

            const midLat = (ownCpa[0] + tgtCpa[0]) / 2;
            const midLon = (ownCpa[1] + tgtCpa[1]) / 2;
            // Label leads with the target name (or MMSI fallback) so a
            // sailor glancing at the chart knows WHICH vessel is on a
            // collision track without having to click the marker.
            // Format:
            //   MV Aurora
            //   0.42 nm · T-5m
            // displayName is the C#-resolved fallback chain (name ->
            // mmsi -> short context); falls back to v.name / v.mmsi
            // explicitly here so the label still renders something
            // useful if the C# pipeline missed a tick.
            const cpaName = v.displayName || v.name || v.mmsi || 'Unknown';
            const labelText = `<strong>${esc(cpaName)}</strong><br>${cpaInfo.cpa.toFixed(2)} nm · T-${cpaInfo.tcpa.toFixed(0)}m`;
            let lbl = aisCpaLabels[v.context];
            if (!lbl) {
                // `interactive: true` lets the label accept pointer events
                // (clicks + touch taps). Without it Leaflet routes every
                // event on the tooltip surface straight to the map below,
                // which is why tapping the CPA chip on the chart previously
                // did nothing. Paired with the click handler below, a tap
                // now opens the target vessel's full popup so the helm
                // can read name / MMSI / SOG / COG / COLREGS role without
                // having to hunt the tiny triangle marker.
                lbl = L.tooltip({
                    permanent: true, direction: 'center', interactive: true,
                    className: `cpa-label ${isDangerEff ? 'cpa-danger' : 'cpa-warn'}`
                }).setLatLng([midLat, midLon]).setContent(labelText).addTo(mapRef);
                aisCpaLabels[v.context] = lbl;
                attachCpaLabelClick(lbl, v.context);
            } else {
                lbl.setLatLng([midLat, midLon]);
                lbl.setContent(labelText);
                const el = lbl.getElement();
                if (el) {
                    el.classList.toggle('cpa-danger', isDangerEff);
                    el.classList.toggle('cpa-warn', !isDangerEff);
                    // setContent rebuilds the DOM, so the click listener
                    // we attached via addEventListener survives on the
                    // container but not if the container itself was
                    // replaced. Re-wiring every update is cheap and
                    // covers the replacement case without bookkeeping.
                    attachCpaLabelClick(lbl, v.context);
                }
            }
        } else {
            removeCpaOverlay(v.context);
        }
    }

    // Remove stale markers.
    for (const ctx of Object.keys(aisMarkers)) {
        if (!seen.has(ctx)) {
            mapRef.removeLayer(aisMarkers[ctx]);
            delete aisMarkers[ctx];
            if (aisVectors[ctx]) { mapRef.removeLayer(aisVectors[ctx]); delete aisVectors[ctx]; }
            delete aisLabels[ctx];
            removeCpaOverlay(ctx);
            removeAisTrail(ctx);
        }
    }
}

function updateAisTrail(ctx, lat, lon) {
    const now = Date.now();
    const hist = aisTrailHistory[ctx] ||= [];
    const last = hist[hist.length - 1];
    if (!last || last.lat !== lat || last.lon !== lon) hist.push({ lat, lon, t: now });

    // Drop points older than the trail window.
    const cutoff = now - AIS_TRAIL_SECONDS * 1000;
    while (hist.length > 0 && hist[0].t < cutoff) hist.shift();

    if (hist.length < 2) return;
    const coords = hist.map(p => [p.lat, p.lon]);
    let line = aisTrailLines[ctx];
    if (!line) {
        line = L.polyline(coords, {
            color: '#94a3b8', weight: 1.2, opacity: 0.45, interactive: false
        }).addTo(mapRef);
        aisTrailLines[ctx] = line;
    } else {
        line.setLatLngs(coords);
    }
}

function removeAisTrail(ctx) {
    if (aisTrailLines[ctx]) { mapRef.removeLayer(aisTrailLines[ctx]); delete aisTrailLines[ctx]; }
    delete aisTrailHistory[ctx];
}

function updateCpaLine(store, ctx, from, to, color) {
    let line = store[ctx];
    if (!line) {
        line = L.polyline([from, to], {
            color, weight: 2, dashArray: '4,4', opacity: 0.9
        }).addTo(mapRef);
        store[ctx] = line;
    } else {
        line.setLatLngs([from, to]);
        line.setStyle({ color });
    }
}

function removeCpaOverlay(ctx) {
    if (aisCpaOwnLines[ctx]) { mapRef.removeLayer(aisCpaOwnLines[ctx]); delete aisCpaOwnLines[ctx]; }
    if (aisCpaTgtLines[ctx]) { mapRef.removeLayer(aisCpaTgtLines[ctx]); delete aisCpaTgtLines[ctx]; }
    if (aisCpaLabels[ctx]) { mapRef.removeLayer(aisCpaLabels[ctx]); delete aisCpaLabels[ctx]; }
}

/**
 * Wires a click / tap on a CPA label to open the target vessel's
 * popup. The popup (built by buildAisPopupHtml) already carries full
 * detail: name, MMSI, callsign, SOG, COG, HDG, bearing / distance
 * from own boat, COLREGS role, external-lookup links, Buddy / Snooze
 * controls, AND a CPA row. The compact CPA label on the chart is a
 * glance-level chip; the full popup is the one-tap drill-in.
 *
 * `stopPropagation` keeps the map from panning or the popup under
 * the label from opening instead. L.DomEvent handles both mouse and
 * touch paths so iPad taps work the same as desktop clicks.
 */
function attachCpaLabelClick(tooltip, vesselContext) {
    const el = tooltip.getElement();
    if (!el) return;
    if (el._onaCpaClickBound) return; // idempotent for setContent rebuilds
    el._onaCpaClickBound = true;
    L.DomEvent.on(el, 'click touchend', (e) => {
        L.DomEvent.stop(e);
        const marker = aisMarkers[vesselContext];
        if (marker && typeof marker.openPopup === 'function') {
            marker.openPopup();
        }
    });
}

/** Pans the map to an AIS vessel and opens its popup. Returns true
 *  when a marker existed; false when the context didn't match anything
 *  (vessel aged out, AIS filter hiding it, deleted since the list
 *  rendered). The C# caller uses the return value to toast + restore
 *  follow so the user isn't left wondering why the tap did nothing. */
export function focusVessel(context) {
    const marker = aisMarkers[context];
    if (!marker || !mapRef) return false;
    const ll = marker.getLatLng();
    mapRef.panTo(ll, { animate: true });
    marker.openPopup();
    return true;
}

// --- guard zone & harbor mode ---

/**
 * Updates collision thresholds used to colour AIS targets and draw
 * the crossing-situation lines. A target whose CPA/TCPA is inside
 * the raw guard zone gets a red line; a target inside
 * guardZone*warningFactor gets amber. Pass warningFactor <= 1 to
 * disable the amber band.
 */
export function setGuardZone(radiusNm, lookaheadMin, warningFactor) {
    guardZoneRadiusNm = radiusNm;
    guardZoneLookaheadMin = lookaheadMin;
    if (typeof warningFactor === 'number' && warningFactor > 1) {
        guardZoneWarningFactor = warningFactor;
    }
    drawGuardZone();
}

// Best-effort layer removal that never throws. Some entries in the
// per-context dicts can be null / undefined under tear-down races
// (a concurrent updateAisTargets that just deleted the key, or a
// disposed Leaflet layer); without the guard map.removeLayer(undefined)
// throws TypeError: Cannot read properties of undefined ('_layerAdd')
// and the whole setHarborMode call rejects -- which the C# side
// then has to roll back via the toast path. Catching here makes the
// JS-side teardown best-effort and lets the C# happy path stay
// green.
function safeRemoveLayer(layer) {
    if (!layer || !mapRef) return;
    try { mapRef.removeLayer(layer); } catch (_) { /* already gone */ }
}

export function setHarborMode(enabled) {
    harborMode = !!enabled;
    if (!mapRef) return;
    if (harborMode) {
        for (const ctx of Object.keys(aisLabels)) {
            try { aisMarkers[ctx]?.unbindTooltip(); } catch (_) { /* marker gone */ }
            delete aisLabels[ctx];
        }
        for (const ctx of Object.keys(aisVectors)) {
            safeRemoveLayer(aisVectors[ctx]);
            delete aisVectors[ctx];
        }
        for (const ctx of Object.keys(aisCpaOwnLines)) {
            safeRemoveLayer(aisCpaOwnLines[ctx]);
            delete aisCpaOwnLines[ctx];
        }
        for (const ctx of Object.keys(aisCpaTgtLines)) {
            safeRemoveLayer(aisCpaTgtLines[ctx]);
            delete aisCpaTgtLines[ctx];
        }
        for (const ctx of Object.keys(aisCpaLabels)) {
            safeRemoveLayer(aisCpaLabels[ctx]);
            delete aisCpaLabels[ctx];
        }
        if (guardZoneRing) {
            safeRemoveLayer(guardZoneRing);
            guardZoneRing = null;
        }
    } else {
        // Coming out of harbor mode: redraw the guard ring at the
        // current radius. AIS labels / vectors / CPA overlays will
        // be re-established by the next updateAisTargets tick.
        drawGuardZone();
    }
}

function drawGuardZone() {
    if (!mapRef) return;
    // Harbor mode hides the ring entirely. The radius itself is not
    // touched (so leaving harbor mode restores the previous setting).
    if (harborMode) {
        if (guardZoneRing) { mapRef.removeLayer(guardZoneRing); guardZoneRing = null; }
        return;
    }
    // Disabled (radius <= 0) - remove the ring entirely instead of
    // shrinking it to a zero-radius invisible point we would still
    // reposition every tick.
    if (guardZoneRadiusNm <= 0) {
        if (guardZoneRing) { mapRef.removeLayer(guardZoneRing); guardZoneRing = null; }
        return;
    }
    const radiusM = guardZoneRadiusNm * 1852;
    if (!guardZoneRing) {
        guardZoneRing = L.circle([selfLat, selfLon], {
            radius: radiusM,
            color: colors.guardWarn,
            weight: 1,
            opacity: 0.5,
            fillColor: colors.guardWarn,
            fillOpacity: 0.04,
            // Non-interactive: the ring no longer gets its own tooltip
            // (user-reported: the "Guard zone (CPA alarm radius)"
            // hover chip was distracting). The legend + Settings
            // already explain what the amber ring is; we don't need
            // to repeat it on hover. Non-interactive also avoids the
            // ring stealing pointer events from anything under it.
            interactive: false,
        }).addTo(mapRef);
    } else {
        guardZoneRing.setLatLng([selfLat, selfLon]);
        guardZoneRing.setRadius(radiusM);
    }
}

export function dispose() {
    for (const ctx of Object.keys(aisMarkers)) delete aisMarkers[ctx];
    for (const ctx of Object.keys(aisVectors)) delete aisVectors[ctx];
    for (const ctx of Object.keys(aisCpaOwnLines)) delete aisCpaOwnLines[ctx];
    for (const ctx of Object.keys(aisCpaTgtLines)) delete aisCpaTgtLines[ctx];
    for (const ctx of Object.keys(aisCpaLabels)) delete aisCpaLabels[ctx];
    for (const ctx of Object.keys(aisTrailLines)) delete aisTrailLines[ctx];
    for (const ctx of Object.keys(aisTrailHistory)) delete aisTrailHistory[ctx];
    for (const ctx of Object.keys(aisLabels)) delete aisLabels[ctx];
    guardZoneRing = null;
    selfLat = 0; selfLon = 0; selfCogRad = null; selfSogMs = null;
    mapRef = null;
    colors = null;
    getDotNetRef = null;
    getEditModeFlags = null;
    editModeAddPoint = null;
    getOwnMmsi = null;
    flagUrl = null;
    rotateMarker = null;
}
