// SignalK delta capture utility.
//
// Connects to a SignalK WebSocket with subscribe=all, dumps every
// incoming envelope as one JSON object per line (jsonl), and exits
// after a configurable duration. Output is replay-friendly: each
// line is exactly what arrived on the wire, so a future test can
// stream it back into a SignalkClient ProcessMessage loop and pin
// real-world behaviour against fresh-from-the-boat data.
//
// Run with:
//   node OnaPlotter.Tests/fixtures/capture-sk.mjs \
//        wss://openplotter.local/signalk/v1/stream \
//        ./openplotter-2min.jsonl 120
//
// Args (positional):
//   1. WebSocket URL (default wss://openplotter.local/signalk/v1/stream)
//   2. Output file path (default ./capture-{ISO}.jsonl)
//   3. Duration seconds (default 120)
//
// Node 22+ has WebSocket built-in; nothing to npm-install. TLS cert
// verification is relaxed because openplotter.local typically uses a
// self-signed cert and we're capturing local lan data, not a public
// API.

import { writeFileSync, appendFileSync } from 'node:fs';

process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

const url = process.argv[2] ?? 'wss://openplotter.local/signalk/v1/stream';
const out = process.argv[3] ?? `./capture-${new Date().toISOString().replace(/[:.]/g, '-')}.jsonl`;
const seconds = Number(process.argv[4] ?? 120);

console.log(`[capture] url=${url}`);
console.log(`[capture] out=${out}`);
console.log(`[capture] seconds=${seconds}`);

writeFileSync(out, '');  // truncate

const ws = new WebSocket(url + '?subscribe=all');

let count = 0;
let bytes = 0;
const start = Date.now();

ws.addEventListener('open', () => {
    console.log('[capture] open; subscribed to all paths');
});

ws.addEventListener('message', (ev) => {
    const text = typeof ev.data === 'string' ? ev.data : new TextDecoder().decode(ev.data);
    appendFileSync(out, text + '\n');
    count++;
    bytes += text.length;
    if (count % 100 === 0) {
        const elapsed = ((Date.now() - start) / 1000).toFixed(1);
        console.log(`[capture] ${elapsed}s ${count} msgs ${(bytes/1024).toFixed(1)} KB`);
    }
});

ws.addEventListener('error', (ev) => {
    console.error('[capture] error', ev);
});

ws.addEventListener('close', (ev) => {
    console.log(`[capture] closed code=${ev.code} reason=${ev.reason}`);
    process.exit(0);
});

setTimeout(() => {
    console.log(`[capture] duration reached; closing. ${count} msgs ${(bytes/1024).toFixed(1)} KB`);
    try { ws.close(1000, 'capture done'); } catch (_) {}
    setTimeout(() => process.exit(0), 250);
}, seconds * 1000);
