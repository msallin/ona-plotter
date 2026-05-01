// Tests for aisLayer.js pure helpers.
// Run with: node --test OnaPlotter/wwwroot/js/aisLayer.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { resolveAisPopupTitle } from './aisLayer.js';

// resolveAisPopupTitle is pinned here because its precedence chain is
// the entire point of the AIS popup-title dedup. A future refactor that
// "obviously simplifies" the branch ordering would silently regress
// the contract that JS-side enrichments (cachedName / callsign / MMSI
// disambiguation) slot in ABOVE the C# canonical when SK only sent us
// an MMSI.

describe('resolveAisPopupTitle', () => {
    it('returns C# canonical when SK has a name (no buddy)', () => {
        // displayName is the C#-built string; for a non-buddy with SK
        // name "Hello" it equals the name verbatim. The resolver MUST
        // return it un-modified so a future double-prefix bug surfaces.
        const t = resolveAisPopupTitle({
            displayName: 'Hello', name: 'Hello',
            callsign: '', mmsi: '244123456', buddy: false, cachedName: undefined,
        });
        assert.equal(t, 'Hello');
    });

    it('returns C# canonical when SK has a name (buddy carries the star)', () => {
        // C# already prefixed the star; the resolver must NOT add a
        // second one, otherwise buddies render as "★ ★ Hello".
        const t = resolveAisPopupTitle({
            displayName: '★ Hello', name: 'Hello',
            callsign: '', mmsi: '', buddy: true, cachedName: undefined,
        });
        assert.equal(t, '★ Hello');
    });

    it('cached name beats C# canonical when SK has only MMSI', () => {
        // The vesselNameCache is a JS-only enrichment populated by an
        // external lookup that the C# layer cannot see. When SK gave us
        // only an MMSI but the cache resolved a real name, prefer the
        // cache so the helm sees "Aurora" instead of "MMSI 244123456".
        const t = resolveAisPopupTitle({
            displayName: '244123456', name: '',
            callsign: '', mmsi: '244123456', buddy: false, cachedName: 'Aurora',
        });
        assert.equal(t, 'Aurora');
    });

    it('cached name with buddy adds the star', () => {
        const t = resolveAisPopupTitle({
            displayName: '★ 244123456', name: '',
            callsign: '', mmsi: '244123456', buddy: true, cachedName: 'Aurora',
        });
        assert.equal(t, '★ Aurora');
    });

    it('callsign beats MMSI fallback when no SK name and no cache', () => {
        const t = resolveAisPopupTitle({
            displayName: '244123456', name: '',
            callsign: 'ZZZ1', mmsi: '244123456', buddy: false, cachedName: undefined,
        });
        assert.equal(t, 'ZZZ1');
    });

    it('callsign with buddy adds the star', () => {
        const t = resolveAisPopupTitle({
            displayName: '★ 244123456', name: '',
            callsign: 'ZZZ1', mmsi: '244123456', buddy: true, cachedName: undefined,
        });
        assert.equal(t, '★ ZZZ1');
    });

    it('MMSI fallback prefixes "MMSI " to the bare id', () => {
        // Without the prefix a bare 9-digit number reads on a chart as
        // a coordinate or distance, not a vessel id. The disambiguation
        // is JS-side because the on-chart label has surrounding context
        // (it sits next to the vessel marker) but the popup title does
        // not.
        const t = resolveAisPopupTitle({
            displayName: '244123456', name: '',
            callsign: '', mmsi: '244123456', buddy: false, cachedName: undefined,
        });
        assert.equal(t, 'MMSI 244123456');
    });

    it('MMSI fallback with buddy adds the star before the prefix', () => {
        const t = resolveAisPopupTitle({
            displayName: '★ 244123456', name: '',
            callsign: '', mmsi: '244123456', buddy: true, cachedName: undefined,
        });
        assert.equal(t, '★ MMSI 244123456');
    });

    it('falls back to Unknown when all signals are absent', () => {
        const t = resolveAisPopupTitle({
            displayName: null, name: '', callsign: '', mmsi: '',
            buddy: false, cachedName: undefined,
        });
        assert.equal(t, 'Unknown');
    });

    it('Unknown with buddy adds the star', () => {
        const t = resolveAisPopupTitle({
            displayName: null, name: '', callsign: '', mmsi: '',
            buddy: true, cachedName: undefined,
        });
        assert.equal(t, '★ Unknown');
    });

    it('cachedName ranks above callsign when both present', () => {
        // Two JS-only enrichments are both available; cache wins because
        // a resolved real name is more useful than a callsign code.
        const t = resolveAisPopupTitle({
            displayName: '244123456', name: '',
            callsign: 'ZZZ1', mmsi: '244123456', buddy: false, cachedName: 'Aurora',
        });
        assert.equal(t, 'Aurora');
    });

    it('SK name beats every fallback even with cache and callsign present', () => {
        // The C# canonical is the most authoritative signal; once SK
        // delivered a real name, no JS enrichment should override it.
        const t = resolveAisPopupTitle({
            displayName: 'Hello', name: 'Hello',
            callsign: 'ZZZ1', mmsi: '244123456', buddy: false, cachedName: 'NotMe',
        });
        assert.equal(t, 'Hello');
    });
});
