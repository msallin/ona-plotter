// Tests for popupHelpers.js. Specifically pins esc() against the
// quote-injection class of bug: vessel names from the SK delta land in
// data-* attributes on popup links (data-mmsi, data-nm, data-ctx -
// see buddyAttrs / snoozeAttrs in aisLayer.js). A name carrying a `"`
// would close the attribute and inject the rest as new attributes; the
// AIS spec's 6-bit ASCII alphabet allows the quote, so this is reachable
// without an attacker on the wire.
//
// Run with: node --test OnaPlotter/wwwroot/js/popupHelpers.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

// JSDOM-free shim. The helpers module touches `document.createElement`,
// which Node doesn't ship. We install a minimal polyfill that mimics the
// browser's textContent -> innerHTML serialization just well enough for
// esc() to exercise the spec it relies on (escape &, <, > - nothing else).
// Keeping it here in the test rather than pulling in JSDOM keeps the JS
// test suite a `node --test` one-liner with zero npm install.
if (typeof globalThis.document === 'undefined') {
    globalThis.document = {
        createElement() {
            return {
                _text: '',
                set textContent(v) { this._text = String(v); },
                get textContent() { return this._text; },
                get innerHTML() {
                    return this._text
                        .replace(/&/g, '&amp;')
                        .replace(/</g, '&lt;')
                        .replace(/>/g, '&gt;');
                },
            };
        },
    };
}

const { esc } = await import('./popupHelpers.js');

describe('esc', () => {
    it('escapes < > & for text-node contexts', () => {
        assert.equal(esc('<b>&'), '&lt;b&gt;&amp;');
    });

    it('escapes double quotes so attr="..." is injection-safe', () => {
        // The bug this pins: a vessel name `foo" onclick="alert(1)`
        // dropped into `data-nm="${esc(name)}"` MUST stay inside the
        // attribute. If esc() ever loses the &quot; replacement, the
        // serialized output here would contain a raw `"` and the
        // resulting markup would parse as TWO attributes.
        const malicious = 'foo" onclick="alert(1)';
        const escaped = esc(malicious);
        assert.ok(!escaped.includes('"'),
            `expected no raw double-quote in escaped output, got: ${escaped}`);
        assert.equal(escaped, 'foo&quot; onclick=&quot;alert(1)');
    });

    it("escapes single quotes so attr='...' is injection-safe", () => {
        // Single-quoted attributes are rare in our templates, but
        // defensive escape costs nothing. Razor-emitted partials we
        // share with the JS layer occasionally use them.
        const escaped = esc("can't");
        assert.ok(!escaped.includes("'"),
            `expected no raw single-quote in escaped output, got: ${escaped}`);
        assert.equal(escaped, 'can&#39;t');
    });

    it('does not double-encode ampersands', () => {
        // Order-of-operations regression guard. If a future tweak runs
        // the quote-replacement BEFORE the textContent round-trip, the
        // already-serialized `&amp;` would become `&amp;amp;`.
        assert.equal(esc('A & B'), 'A &amp; B');
        assert.equal(esc('"&"'), '&quot;&amp;&quot;');
    });

    it('renders typical AIS vessel-name characters unchanged', () => {
        // Sanity: ASCII alpha + digits + the punctuation AIS allows
        // (space, `.`, `-`, `/`) round-trips visibly. A future esc()
        // that over-escaped (e.g. encoded every non-alphanumeric)
        // would render "MV TESTER" as something unreadable on the
        // banner.
        assert.equal(esc('MV TESTER-1'), 'MV TESTER-1');
        assert.equal(esc('R/V SALTY.BREEZE'), 'R/V SALTY.BREEZE');
    });

    it('handles non-string inputs by string-coercing', () => {
        // SK deltas can deliver numbers (MMSI as 211234567) into a
        // call site that built a string template. document.textContent
        // setter coerces; the test pins that the call doesn't crash
        // and produces something sensible.
        assert.equal(esc(42), '42');
    });
});
