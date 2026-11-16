// Tests for platform/audioAlert.js AudioContext unlock / resume.
// Run with: node --test OnaPlotter/wwwroot/js/audioAlert.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

// Stand-in AudioContext: resume() is controlled per test, statechange
// is dispatched by the test to mimic iOS interruptions.
class FakeAudioContext extends EventTarget {
    static instances = [];
    static resumeBehaviour = 'ok';     // 'ok' | 'reject'
    constructor() {
        super();
        this.state = 'suspended';
        this.resumeCalls = 0;
        this.currentTime = 0;
        this.destination = {};
        FakeAudioContext.instances.push(this);
    }
    resume() {
        this.resumeCalls++;
        if (FakeAudioContext.resumeBehaviour === 'reject') {
            return Promise.reject(new Error('NotAllowedError'));
        }
        this.state = 'running';
        return Promise.resolve();
    }
    setState(state) {
        this.state = state;
        this.dispatchEvent(new Event('statechange'));
    }
    createOscillator() { return { connect() {}, start() {}, stop() {}, frequency: {} }; }
    createGain() {
        return { connect() {}, gain: { setValueAtTime() {}, exponentialRampToValueAtTime() {} } };
    }
}

globalThis.window = { AudioContext: FakeAudioContext };
globalThis.document = new EventTarget();

const audio = await import('./platform/audioAlert.js');
const tap = () => document.dispatchEvent(new Event('pointerdown'));
const settle = () => new Promise(r => setTimeout(r, 0));

describe('audio alert unlock', () => {
    it('the first gesture creates and resumes the context', async () => {
        tap();
        await settle();
        const ctx = FakeAudioContext.instances.at(-1);
        assert.equal(ctx.state, 'running');
    });

    it('re-arms after an iOS interruption and resumes on the next tap', async () => {
        const ctx = FakeAudioContext.instances.at(-1);
        ctx.setState('interrupted');
        const before = ctx.resumeCalls;
        tap();
        await settle();
        assert.equal(ctx.resumeCalls, before + 1);
        assert.equal(ctx.state, 'running');
    });

    it('startAlarm resumes an interrupted context, not only a suspended one', async () => {
        const ctx = FakeAudioContext.instances.at(-1);
        ctx.state = 'interrupted';
        const before = ctx.resumeCalls;
        audio.startAlarm(60000);
        audio.stopAlarm();
        await settle();
        assert.ok(ctx.resumeCalls > before);
    });

    it('a rejected resume is handled and the next gesture retries', async () => {
        const ctx = FakeAudioContext.instances.at(-1);
        FakeAudioContext.resumeBehaviour = 'reject';
        ctx.setState('suspended');
        audio.startAlarm(60000);
        audio.stopAlarm();
        await settle();   // an unhandled rejection would fail the run
        FakeAudioContext.resumeBehaviour = 'ok';
        const before = ctx.resumeCalls;
        tap();
        await settle();
        assert.equal(ctx.resumeCalls, before + 1);
        assert.equal(ctx.state, 'running');
    });

    it('a running context is left alone', async () => {
        const ctx = FakeAudioContext.instances.at(-1);
        const before = ctx.resumeCalls;
        audio.startAlarm(60000);
        assert.equal(audio.isAlarmActive(), true);
        audio.stopAlarm();
        assert.equal(audio.isAlarmActive(), false);
        await settle();
        assert.equal(ctx.resumeCalls, before);
    });
});
