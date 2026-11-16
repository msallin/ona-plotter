// Tests for keyboardShortcuts.js.
// Run with: node --test OnaPlotter/wwwroot/js/keyboardShortcuts.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { isTypingTarget, shortcutKey } from './keyboardShortcuts.js';

describe('isTypingTarget', () => {
    it('form fields keep their keys', () => {
        for (const tagName of ['INPUT', 'TEXTAREA', 'SELECT']) {
            assert.equal(isTypingTarget({ tagName }), true, tagName);
        }
    });

    it('contenteditable keeps its keys', () => {
        assert.equal(isTypingTarget({ tagName: 'DIV', isContentEditable: true }), true);
    });

    it('the map and buttons do not', () => {
        assert.equal(isTypingTarget({ tagName: 'DIV', isContentEditable: false }), false);
        assert.equal(isTypingTarget({ tagName: 'BUTTON' }), false);
        assert.equal(isTypingTarget({ tagName: 'BODY' }), false);
    });

    it('a missing target is not typing', () => {
        assert.equal(isTypingTarget(null), false);
        assert.equal(isTypingTarget(undefined), false);
    });
});

describe('shortcutKey', () => {
    it('lower-cases a letter', () => {
        assert.equal(shortcutKey({ key: 'F' }), 'f');
        assert.equal(shortcutKey({ key: 'n' }), 'n');
    });

    it('returns null for autofill keydowns without a key', () => {
        assert.equal(shortcutKey({}), null);
        assert.equal(shortcutKey({ key: undefined }), null);
        assert.equal(shortcutKey({ key: '' }), null);
    });

    it('keeps named keys', () => {
        assert.equal(shortcutKey({ key: 'Escape' }), 'escape');
    });
});
