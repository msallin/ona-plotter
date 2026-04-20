// Canned SignalK delta pump for OnaPlotter dev.
//
// Posts a vessel track (lat/lon circling a fixed centre), wind, depth,
// course-over-ground, and a handful of AIS targets on a fixed loop.
// Deterministic from FAKE_SEED so the MOB / CPA / shallow scenarios
// are reproducible across runs and CI.
//
// Injection path: raw TCP on port 10111, newline-delimited SignalK
// delta JSON. signalk-server exposes this via a pipedProvider (see
// settings.json "fake-data-tcp") which wraps providers/tcp + liner
// + from_json. No auth -- this is the standard input pattern for
// NMEA multiplexers / hardware feeds; HTTP writes require auth.

import net from 'node:net';

const SK_HOST = process.env.SK_HOST || 'sk';
const SK_TCP_PORT = parseInt(process.env.SK_TCP_PORT || '10111', 10);
const TICK_MS = parseInt(process.env.FAKE_TICK_MS || '1000', 10);
const SEED = parseInt(process.env.FAKE_SEED || '42', 10);

// --- Deterministic PRNG so a given seed produces the same run ---
function mulberry32(a) {
    return function () {
        let t = (a += 0x6D2B79F5);
        t = Math.imul(t ^ (t >>> 15), t | 1);
        t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}
const rng = mulberry32(SEED);

// --- Own-vessel state ---
// Circle a small loop near the Caribbean (43.2N / 7.6W-ish). Gives a
// track that exercises the TrackBuffer and makes AIS targets pass
// within guard-zone distance.
const CENTRE_LAT = 43.2;
const CENTRE_LON = -7.6;
const CIRCLE_RADIUS_DEG = 0.01;         // ~1 km
const CIRCLE_PERIOD_SEC = 600;          // 10 min per lap
const BOAT_SPEED_MS = 2.57;             // ~5 kn

let tick = 0;

function ownVesselDelta() {
    const t = (tick * TICK_MS) / 1000;
    const theta = (2 * Math.PI * t) / CIRCLE_PERIOD_SEC;
    const lat = CENTRE_LAT + CIRCLE_RADIUS_DEG * Math.sin(theta);
    const lon = CENTRE_LON + CIRCLE_RADIUS_DEG * Math.cos(theta);
    // Tangent to the circle = heading. Add a tiny random jitter so the
    // HUD compass doesn't read perfectly stable (which looks unreal).
    const cog = (theta + Math.PI / 2) % (2 * Math.PI);
    const depth = 8.0 + 4.0 * Math.sin(theta * 3) + (rng() - 0.5) * 0.3;
    const twd = Math.PI;                                 // southerly
    const tws = 7.5 + (rng() - 0.5) * 0.4;               // ~15 kn
    const awa = 0.6 + (rng() - 0.5) * 0.1;
    const aws = 9.0 + (rng() - 0.5) * 0.3;

    const values = [
        { path: 'navigation.position', value: { latitude: lat, longitude: lon } },
        { path: 'navigation.speedOverGround', value: BOAT_SPEED_MS },
        { path: 'navigation.courseOverGroundTrue', value: cog },
        { path: 'navigation.headingTrue', value: cog },
        { path: 'environment.depth.belowTransducer', value: depth },
        { path: 'environment.wind.directionTrue', value: twd },
        { path: 'environment.wind.speedTrue', value: tws },
        { path: 'environment.wind.angleApparent', value: awa },
        { path: 'environment.wind.speedApparent', value: aws },
    ];
    // Publish design data once per minute so OnaPlotter's
    // DraftFromSignalK auto-fills without hammering the bus. Static
    // config lives in docker/signalk-config/settings.json -> vessel.draft
    // but those don't emit deltas, so repeat here.
    if (tick % 60 === 0) {
        values.push({ path: 'design.draft.current', value: 1.8 });
        values.push({ path: 'design.draft.maximum', value: 2.0 });
    }
    return { context: 'vessels.self', updates: [{ timestamp: new Date().toISOString(), values }] };
}

// --- AIS targets ---
// Five vessels at fixed offsets. One crosses near the guard zone on a
// predictable cadence; one loiters (moored-ish); the rest are steady
// courses. The MMSIs are in the reserved 'dev' range per EN 303213.
const AIS_TARGETS = [
    {
        name: 'SALTY BREEZE', mmsi: '211999001',
        shipType: 36, callsign: 'DEV01',
        offsetLat: 0.015, offsetLon: 0.010,
        sog: 2.0, cog: Math.PI * 0.75,
    },
    {
        name: 'ATLANTIC STAR', mmsi: '211999002',
        shipType: 70, callsign: 'DEV02',
        offsetLat: -0.010, offsetLon: 0.020,
        sog: 6.5, cog: Math.PI * 1.1,
    },
    {
        name: 'MOORED PETE', mmsi: '211999003',
        shipType: 30, callsign: 'DEV03',
        offsetLat: 0.003, offsetLon: -0.005,
        sog: 0.0, cog: 0,
    },
    {
        name: 'FERRY JANET', mmsi: '211999004',
        shipType: 60, callsign: 'DEV04',
        offsetLat: -0.020, offsetLon: -0.012,
        sog: 10.0, cog: Math.PI * 0.3,
    },
    {
        name: 'FISH KING', mmsi: '211999005',
        shipType: 30, callsign: 'DEV05',
        offsetLat: 0.020, offsetLon: -0.018,
        sog: 3.0, cog: Math.PI * 1.8,
    },
];

function aisDeltas() {
    const t = (tick * TICK_MS) / 1000;
    return AIS_TARGETS.map(v => {
        // Targets drift from their offsets per their cog/sog so they
        // actually move. 1 deg lat ~ 111 km, 1 m/s = 1 m / sec.
        const metresPerSec = v.sog;
        const dMetres = metresPerSec * t;
        const latOffMoved = v.offsetLat + (Math.cos(v.cog) * dMetres) / 111000;
        const lonOffMoved = v.offsetLon + (Math.sin(v.cog) * dMetres)
            / (111000 * Math.cos(CENTRE_LAT * Math.PI / 180));
        return {
            context: `vessels.urn:mrn:imo:mmsi:${v.mmsi}`,
            updates: [{
                timestamp: new Date().toISOString(),
                values: [
                    { path: '', value: { name: v.name, mmsi: v.mmsi } },
                    { path: 'navigation.position', value: {
                        latitude: CENTRE_LAT + latOffMoved,
                        longitude: CENTRE_LON + lonOffMoved
                    }},
                    { path: 'navigation.speedOverGround', value: v.sog },
                    { path: 'navigation.courseOverGroundTrue', value: v.cog },
                    { path: 'design.aisShipType', value: { id: v.shipType } },
                    { path: 'communication.callsignVhf', value: v.callsign },
                ]
            }]
        };
    });
}

// --- TCP server ---
// signalk-server's providers/tcp is a CLIENT -- it connects to a
// remote host:port and reads newline-delimited SignalK JSON. We are
// that remote. Listen on LISTEN_PORT (default 10111); every connected
// client gets each tick's deltas written as a single newline per delta.
// In practice the only client is the signalk-server container, but
// the multi-client design lets a dev attach `nc` or `websocat` for
// debugging.
const LISTEN_PORT = parseInt(process.env.FAKE_LISTEN_PORT || '10111', 10);
const clients = new Set();

const server = net.createServer((socket) => {
    clients.add(socket);
    socket.on('close', () => clients.delete(socket));
    socket.on('error', () => clients.delete(socket));
    console.log(`[fake-data] client connected (${clients.size} total)`);
});
server.listen(LISTEN_PORT, '0.0.0.0', () => {
    console.log(`[fake-data] listening on 0.0.0.0:${LISTEN_PORT}`);
});

function sendDelta(delta) {
    const line = JSON.stringify(delta) + '\n';
    for (const socket of clients) {
        try { socket.write(line); }
        catch { /* socket died; will be removed on close/error */ }
    }
}

// --- Main loop ---
console.log(`[fake-data] starting (tick=${TICK_MS}ms, seed=${SEED})`);

setInterval(() => {
    tick++;
    sendDelta(ownVesselDelta());
    for (const d of aisDeltas()) sendDelta(d);
}, TICK_MS);
