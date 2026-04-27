# SignalK delta fixtures

`*.jsonl` files in this folder are raw captures from a live SignalK
WebSocket (`subscribe=all`), one envelope per line. Replay them
through `SignalkClient.ProcessMessage` to drive a test against
real-world data without standing up a fake server.

## Capture more

```bash
node capture-sk.mjs \
    wss://openplotter.local/signalk/v1/stream \
    ./openplotter-{whatever}.jsonl 120
```

Args:

1. WebSocket URL (default `wss://openplotter.local/signalk/v1/stream`)
2. Output file path (default `./capture-{ISO}.jsonl`)
3. Duration seconds (default `120`)

Node 22+ has `WebSocket` built-in; nothing to `npm install`. TLS cert
verification is relaxed because openplotter.local typically uses a
self-signed cert.

## What's here

- `capture-sk.mjs` — the capture utility above
- `openplotter-2min.jsonl` — 2-minute capture from the helm's boat,
  ~12 k envelopes, ~3.5 MB. Used as the seed corpus for replay-based
  tests; safe to regenerate (the SK paths the test code reads from
  are stable across captures).
