// Tests for the focus-trap helpers in platform/viewport.js.
// Run with: node --test OnaPlotter/wwwroot/js/focusTrap.test.js
//
// The alarm-log dialog is aria-modal; browsers don't trap focus for
// that, so Tab from the last control must wrap to the first (and
// Shift+Tab from the first to the last) instead of escaping into the
// chart underneath on an iPad with a keyboard. The wrap decision is
// pure; the DOM wrapper is exercised with a minimal fake element.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { focusWrapTarget, attachFocusTrap } from './platform/viewport.js';

const a = { name: 'a' }, b = { name: 'b' }, c = { name: 'c' };

describe('focusWrapTarget', () => {
    it('wraps Tab from the last control to the first', () => {
        assert.equal(focusWrapTarget([a, b, c], c, false), a);
    });
    it('wraps Shift+Tab from the first control to the last', () => {
        assert.equal(focusWrapTarget([a, b, c], a, true), c);
    });
    it('leaves mid-list moves to the browser', () => {
        assert.equal(focusWrapTarget([a, b, c], b, false), null);
        assert.equal(focusWrapTarget([a, b, c], b, true), null);
    });
    it('pulls focus back in when it sits outside the list', () => {
        assert.equal(focusWrapTarget([a, b, c], { name: 'outside' }, false), a);
        assert.equal(focusWrapTarget([a, b, c], { name: 'outside' }, true), c);
    });
    it('returns null when there is nothing focusable', () => {
        assert.equal(focusWrapTarget([], a, false), null);
    });
});

function fakePanel(focusables) {
    const listeners = {};
    return {
        focusables,
        addEventListener(type, fn) { listeners[type] = fn; },
        removeEventListener(type) { delete listeners[type]; },
        querySelectorAll() { return focusables; },
        contains(el) { return focusables.includes(el); },
        fire(type, ev) { listeners[type]?.(ev); },
        has(type) { return type in listeners; },
    };
}

function fakeKey(key, shiftKey) {
    return { key, shiftKey, prevented: false, preventDefault() { this.prevented = true; } };
}

describe('attachFocusTrap', () => {
    it('wraps and prevents the default Tab at the boundary', () => {
        let focused = null;
        const els = [a, b, c].map(x => ({ ...x, focus() { focused = this; } }));
        const panel = fakePanel(els);
        const doc = { activeElement: els[2] };
        attachFocusTrap(panel, doc);

        const ev = fakeKey('Tab', false);
        panel.fire('keydown', ev);

        assert.equal(ev.prevented, true);
        assert.equal(focused, els[0]);
    });
    it('does not interfere mid-list or with other keys', () => {
        const els = [a, b, c].map(x => ({ ...x, focus() {} }));
        const panel = fakePanel(els);
        attachFocusTrap(panel, { activeElement: els[1] });

        const tab = fakeKey('Tab', false);
        const esc = fakeKey('Escape', false);
        panel.fire('keydown', tab);
        panel.fire('keydown', esc);

        assert.equal(tab.prevented, false);
        assert.equal(esc.prevented, false);
    });
    it('is idempotent and tolerates a missing panel', () => {
        const panel = fakePanel([]);
        attachFocusTrap(panel, { activeElement: null });
        attachFocusTrap(panel, { activeElement: null });
        assert.equal(panel.has('keydown'), true);
        assert.doesNotThrow(() => attachFocusTrap(null));
    });
});
