// Audio alert via Web Audio API for connection-loss warning.
// Plays a short repeating beep pattern that sounds like a marine alarm.

let audioCtx = null;
let alarmInterval = null;
let userGestureSeen = false;

function ensureContext() {
    if (!audioCtx) {
        audioCtx = new (window.AudioContext || window.webkitAudioContext)();
    }
    return audioCtx;
}

// Browsers (Firefox, Chrome, Safari) all enforce an autoplay policy:
// an AudioContext created before the page has seen a user gesture
// stays in 'suspended' state and prints a console warning ("An
// AudioContext was prevented from starting automatically. It must
// be created or resumed after a user gesture on the page.")
// when audio is started.
//
// The chartplotter's alarm path is reactive (alarms can fire from
// SignalR delta events long after page mount, with no preceding
// click), so we pre-warm the context on the FIRST pointerdown /
// keydown / touchstart - by then it's already running and the
// next startAlarm call doesn't trip the autoplay warning. The
// listeners self-remove after one fire so they don't stay hot.
function warmUp() {
    if (userGestureSeen) return;
    userGestureSeen = true;
    try {
        const ctx = ensureContext();
        if (ctx.state === 'suspended') ctx.resume();
    } catch (_) { /* audio API unsupported in this context */ }
}
// `once: true` removes the listener automatically after first fire.
// Capture phase + passive so we don't interfere with downstream
// handlers (Leaflet pan, Blazor click) that might want to
// preventDefault.
if (typeof window !== 'undefined' && typeof document !== 'undefined') {
    const opts = { once: true, capture: true, passive: true };
    document.addEventListener('pointerdown', warmUp, opts);
    document.addEventListener('keydown',     warmUp, opts);
    document.addEventListener('touchstart',  warmUp, opts);
}

function playBeep(frequency, durationMs) {
    const ctx = ensureContext();
    if (ctx.state === 'suspended') ctx.resume();

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
