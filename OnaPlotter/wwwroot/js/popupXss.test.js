// Popup / label HTML builders must escape every server-supplied string.
// Names, callsigns and OSM tags come off the air or from third parties;
// Leaflet writes popup and tooltip content with innerHTML.
// Run with: node --test OnaPlotter/wwwroot/js/popupXss.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { buildSelfPopupHtml } from './ownBoatPopup.js';
import { buildAtonPopupHtml } from './atonLayer.js';
import { buildAisPopupHtml } from './aisLayer.js';
import { buildMarinePoiPopupHtml } from './marinePoiLayer.js';

const PAYLOAD = '<img src=x onerror=alert(1)>';
const ATTR_PAYLOAD = '" onmouseover="alert(1)';

// No raw tag and no attribute break-out survives.
function assertInert(html) {
    assert.ok(!html.includes('<img src=x'), `raw tag in: ${html}`);
    assert.ok(!html.includes('" onmouseover="'), `attribute break-out in: ${html}`);
}

describe('own-boat popup', () => {
    it('escapes MMSI and callsign from the SignalK feed', () => {
        const html = buildSelfPopupHtml({ lat: 54, lon: 11 }, PAYLOAD, PAYLOAD, () => 'flag.png');
        assertInert(html);
        assert.ok(html.includes('&lt;img src=x onerror=alert(1)&gt;'));
    });

    it('escapes the flag URL attribute', () => {
        const html = buildSelfPopupHtml({}, '211000000', null, () => `x${ATTR_PAYLOAD}`);
        assertInert(html);
    });

    it('renders a normal identity', () => {
        // Realistic: German yacht with MMSI and callsign.
        const html = buildSelfPopupHtml({ sogMs: 2.5 }, '211234560', 'DA1234', () => 'f.png');
        assert.ok(html.includes('<td>211234560</td>'));
        assert.ok(html.includes('<td>DA1234</td>'));
        assert.ok(html.includes('4.9 kn'));
    });

    it('omits identity rows while unknown', () => {
        const html = buildSelfPopupHtml({}, null, null, () => 'f.png');
        assert.ok(!html.includes('MMSI'));
        assert.ok(!html.includes('ais-popup-flag'));
    });
});

describe('AtoN popup', () => {
    it('escapes title, subtitle and detail', () => {
        assertInert(buildAtonPopupHtml({ title: PAYLOAD, subtitle: PAYLOAD, detail: PAYLOAD }));
    });
});

describe('AIS popup', () => {
    it('escapes every identity and COLREGS string', () => {
        const rec = {
            v: {
                context: `vessels.urn:mrn:imo:mmsi:${ATTR_PAYLOAD}`,
                lastAisSeen: '2026-05-17T11:42:13Z',
                colregsLabel: PAYLOAD, colregsRole: PAYLOAD, snoozable: true,
            },
            s: { title: PAYLOAD, subtitle: PAYLOAD, name: ATTR_PAYLOAD, mmsi: '' },
        };
        assertInert(buildAisPopupHtml(rec));
    });
});

describe('marine POI popup', () => {
    it('escapes name, category and tags', () => {
        assertInert(buildMarinePoiPopupHtml({
            id: `n1${ATTR_PAYLOAD}`, name: PAYLOAD, category: PAYLOAD,
            tags: { operator: PAYLOAD },
        }));
    });

    it('drops a javascript: website link', () => {
        const html = buildMarinePoiPopupHtml({
            id: 'n1', category: 'marina', tags: { website: 'javascript:alert(1)' },
        });
        assert.ok(!html.includes('javascript:'));
        assert.ok(!html.includes('Website'));
    });

    it('keeps an https website link', () => {
        const html = buildMarinePoiPopupHtml({
            id: 'n1', category: 'marina', tags: { website: 'https://marina.example/' },
        });
        assert.ok(html.includes('href="https://marina.example/"'));
    });
});
