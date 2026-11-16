/**
 * Own-boat popup HTML: the AIS popup's shape minus the vessel-lookup
 * links and buddy toggle. Rebuilt on every popupopen from the boat
 * marker's latest fix so the numbers track nav updates live. MMSI and
 * callsign come off the SignalK feed and are escaped like any other
 * server string.
 */

import { DEG } from './geoMath.js';
import { MS_TO_KNOTS } from './format.js';
import { esc } from './popupHelpers.js';

/**
 * @param {{lat?: number, lon?: number, sogMs?: number, cogRad?: number, headingRad?: number}} data
 * @param {string|null} ownMmsi
 * @param {string|null} ownCallsign
 * @param {(mmsi: string) => string} flagUrl signalk-flags image URL for an MMSI
 */
export function buildSelfPopupHtml(data, ownMmsi, ownCallsign, flagUrl) {
    const lat = data.lat, lon = data.lon;
    const sog = data.sogMs != null ? (data.sogMs * MS_TO_KNOTS).toFixed(1) : '--';
    const cogDeg = data.cogRad != null ? (data.cogRad * DEG).toFixed(0) : '--';
    const hdgDeg = data.headingRad != null ? (data.headingRad * DEG).toFixed(0) : '--';
    const pos = (lat != null && lon != null)
        ? `${lat.toFixed(5)}, ${lon.toFixed(5)}`
        : '--';
    // Country flag from the signalk-flags plugin when the own MMSI is
    // known; hidden on error (plugin not installed).
    const flagHtml = ownMmsi
        ? `<img class="ais-popup-flag" src="${esc(flagUrl(ownMmsi))}" alt="" onerror="this.style.display='none'">`
        : '';
    // Identity rows only when known. The helm reads the MMSI off the
    // popup onto the VHF mic during distress comms.
    const mmsiRow = ownMmsi
        ? `<tr><td>MMSI</td><td>${esc(ownMmsi)}</td></tr>`
        : '';
    const callsignRow = ownCallsign
        ? `<tr><td>Call</td><td>${esc(ownCallsign)}</td></tr>`
        : '';
    return `<div class="ais-popup-content">` +
        `<div class="ais-popup-title">${flagHtml}&#9733; Own boat</div>` +
        `<table class="ais-popup-table">` +
            `<tr><td>Pos</td><td>${pos}</td></tr>` +
            `<tr><td>SOG</td><td>${sog} kn</td></tr>` +
            `<tr><td>COG</td><td>${cogDeg}&deg;</td></tr>` +
            `<tr><td>HDG</td><td>${hdgDeg}&deg;</td></tr>` +
            mmsiRow +
            callsignRow +
        `</table>` +
        `</div>`;
}
