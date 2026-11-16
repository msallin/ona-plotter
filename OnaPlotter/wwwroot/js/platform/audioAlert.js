// Audio alert via Web Audio API for connection-loss warning.
// Plays a short repeating beep pattern that sounds like a marine alarm.

let audioCtx = null;
let alarmInterval = null;
let unlockArmed = false;

// Browsers start an AudioContext 'suspended' until the page has seen a
// user gesture, and iOS later moves a running one to 'interrupted'
// (phone call, Siri, another app's audio) or back to 'suspended'
// (backgrounding). Alarms fire from SignalK deltas with no preceding
// tap, so a gesture listener stays armed whenever the context isn't
// running: the next tap anywhere revives it before an alarm needs it.
// Capture + passive so Leaflet pans and Blazor clicks are untouched.
const GESTURES = ['pointerdown', 'keydown', 'touchstart'];
const GESTURE_OPTS = { capture: true, passive: true };

function ensureContext() {
    if (!audioCtx || audioCtx.state === 'closed') {
        const Ctor = window.AudioContext || window.webkitAudioContext;
        if (!Ctor) return null;
        audioCtx = new Ctor();
        audioCtx.addEventListener('statechange', onStateChange);
    }
    return audioCtx;
}

function onStateChange() {
    if (audioCtx && audioCtx.state !== 'running') armGestureUnlock();
}

// Resumes on any non-running state, not just 'suspended': WebKit reports
// 'interrupted'. resume() rejects without a gesture; the unlock then
// waits for one.
function resumeContext(ctx) {
    if (ctx.state === 'running') return;
    let pending;
    try {
        pending = ctx.resume();
    } catch (_) {
        armGestureUnlock();
        return;
    }
    if (pending && typeof pending.then === 'function') {
        pending.then(
            () => { if (ctx.state !== 'running') armGestureUnlock(); },
            () => armGestureUnlock());
    }
}

function onGesture() {
    disarmGestureUnlock();
    try {
        const ctx = ensureContext();
        if (ctx) resumeContext(ctx);
    } catch (_) { /* audio API unsupported in this context */ }
}

function armGestureUnlock() {
    if (unlockArmed || typeof document === 'undefined') return;
    unlockArmed = true;
    for (const type of GESTURES) document.addEventListener(type, onGesture, GESTURE_OPTS);
}

function disarmGestureUnlock() {
    if (!unlockArmed) return;
    unlockArmed = false;
    for (const type of GESTURES) document.removeEventListener(type, onGesture, GESTURE_OPTS);
}

armGestureUnlock();

function playBeep(frequency, durationMs) {
    const ctx = ensureContext();
    if (!ctx) return;
    resumeContext(ctx);

    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.connect(gain);
    gain.connect(ctx.destination);

    osc.type = 'square';
    osc.frequency.value = frequency;
    gain.gain.value = 0.15;

    // Fade out to avoid click
    gain.gain.setValueAtTime(0.15, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + durationMs / 1000);

    osc.start(ctx.currentTime);
    osc.stop(ctx.currentTime + durationMs / 1000);
}

// Plays a two-tone alarm beep (high-low pattern).
function playAlarmBeep() {
    playBeep(880, 200);
    setTimeout(() => playBeep(660, 200), 250);
}

// Start a repeating alarm that beeps every intervalMs.
export function startAlarm(intervalMs) {
    stopAlarm();
    playAlarmBeep();
    alarmInterval = setInterval(playAlarmBeep, intervalMs || 3000);
}

// Stop the repeating alarm.
export function stopAlarm() {
    if (alarmInterval) {
        clearInterval(alarmInterval);
        alarmInterval = null;
    }
}

// Returns true if the alarm is currently active.
export function isAlarmActive() {
    return alarmInterval !== null;
}
