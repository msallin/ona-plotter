// Audio alert via Web Audio API for connection-loss warning.
// Plays a short repeating beep pattern that sounds like a marine alarm.

let audioCtx = null;
let alarmInterval = null;

function ensureContext() {
    if (!audioCtx) {
        audioCtx = new (window.AudioContext || window.webkitAudioContext)();
    }
    return audioCtx;
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
