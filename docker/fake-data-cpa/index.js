// CPA / TCPA visualisation test scenario.
//
// Emits own boat moving straight north + 7 AIS targets carefully
// positioned for the new two-tier classifier defaults
// (alarm: CPA <= 0.1 nm AND TCPA <= 30 min; awareness: CPA <= 1.0 nm
// AND TCPA <= 30 min). Outcome on first paint:
//
//   - 3 vessels OUTSIDE both zones (no overlay, default chip colour)
//   - 2 vessels in AWARENESS (amber chip, overlay shows on click)
//   - 2 vessels in ALARM (red chip, always-on overlay + audible alarm)
//
// Geometry is stable for the first ~5-10 minutes; after that own boat
// passes through the encounter area and the relative positions evolve.
// Restart the container (or refresh the page) to reset.
//
// Wire protocol same as ../fake-data/index.js: SK delta JSON on a
// newline-delimited TCP socket the SK server connects to via its
// providers/tcp pipedProvider.

import net from 'node:net';

const TICK_MS = parseInt(process.env.FAKE_TICK_MS || '1000', 10);

// Own boat starts here heading straight north. Position at lat 43.2 N,
// 7.6 W (off the Portuguese coast - same area as the dev scenario so
// the helm can pan / zoom familiarly). 5 kn = 2.5722 m/s.
const OWN_LAT = 43.20;
const OWN_LON = -7.60;
const OWN_COG = 0;                       // due north
const OWN_SOG_MS = 2.5722;               // 5 kn

// Conversion factors at this latitude. 1 nm = 1/60 deg latitude;
// longitude is squeezed by cos(lat). Precomputed once at module load.
const NM_TO_LAT = 1.0 / 60.0;
const NM_TO_LON = 1.0 / (60.0 * Math.cos(OWN_LAT * Math.PI / 180.0));

// Each target carries its initial offset relative to own's starting
// position (in nautical miles, east + north), plus its own COG and
// SOG. The tick loop walks own + each target forward at their
// respective velocities and emits the current absolute lat/lon. The
// chosen geometries fall out as:
//
//   Vessel               | Initial offset       | Course      | Speed | Outcome
//   ---------------------|----------------------|-------------|-------|----------
//   HEADON ALARM         |  0 nm E,  1.5 nm N   | south (180) | 5 kn  | ALARM   (CPA ~0 nm at TCPA ~9 min, head-on)
//   NEARMISS ALARM       |  0.027 E, 1   N      | south (180) | 5 kn  | ALARM   (CPA ~0.03 nm at TCPA ~6 min)
//   CROSSING AWARENESS   |  0.5  E,  1   N      | west  (270) | 5 kn  | AWARE   (CPA ~0.35 nm at TCPA ~9 min)
//   PARALLEL AWARENESS   |  0.7  E,  1   N      | south (180) | 5 kn  | AWARE   (CPA = 0.7 nm at TCPA ~6 min)
//   FAR PARALLEL         |  5    E,  0   N      | north (000) | 5 kn  | OUTSIDE (same heading + speed - never closes)
//   DIVERGING            |  0    E, -1   N      | south (180) | 5 kn  | OUTSIDE (CPA already passed)
//   SLOW DRIFTER         | -3    E,  0   N      | north (000) | 2 kn  | OUTSIDE (CPA ~3 nm - too far)
const AIS_TARGETS = [
    // --- ALARM TIER (red chip + always-on overlay + audible klaxon) ---
    {
        name: 'HEADON ALARM', mmsi: '211999101',
        shipType: 70, callsign: 'ALM01',
        offsetEastNm: 0.0, offsetNorthNm: 1.5,
        cog: Math.PI,                 // 180 deg = due south
        sogMs: 2.5722,                // 5 kn
    },
    {
        name: 'NEARMISS ALARM', mmsi: '211999102',
        shipType: 60, callsign: 'ALM02',
        offsetEastNm: 0.027, offsetNorthNm: 1.0,
        cog: Math.PI,                 // south, 50 m east offset for non-zero CPA
        sogMs: 2.5722,
    },
    // --- AWARENESS TIER (amber chip, overlay on click only) ---
    {
        name: 'CROSSING AWARENESS', mmsi: '211999201',
        shipType: 36, callsign: 'AWR01',
        offsetEastNm: 0.5, offsetNorthNm: 1.0,
        cog: (3 * Math.PI) / 2,       // 270 deg = west, crossing own's path
        sogMs: 2.5722,
    },
    {
        name: 'PARALLEL AWARENESS', mmsi: '211999202',
        shipType: 30, callsign: 'AWR02',
        offsetEastNm: 0.7, offsetNorthNm: 1.0,
        cog: Math.PI,                 // south, parallel pass at 0.7 nm
        sogMs: 2.5722,
    },
    // --- OUTSIDE BOTH ZONES (no overlay - default chip colour) ---
    {
        name: 'FAR PARALLEL', mmsi: '211999301',
        shipType: 70, callsign: 'OUT01',
        offsetEastNm: 5.0, offsetNorthNm: 0.0,
        cog: 0,                       // same heading + same speed as own = never closes
        sogMs: 2.5722,
    },
    {
        name: 'DIVERGING', mmsi: '211999302',
        shipType: 60, callsign: 'OUT02',
        offsetEastNm: 0.0, offsetNorthNm: -1.0,
        cog: Math.PI,                 // already south of own, moving south = CPA already passed
        sogMs: 2.5722,
    },
    {
        name: 'SLOW DRIFTER', mmsi: '211999303',
        shipType: 30, callsign: 'OUT03',
        offsetEastNm: -3.0, offsetNorthNm: 0.0,
        cog: 0,                       // 3 nm west, slow drift north - CPA ~3 nm forever
        sogMs: 1.0289,                // 2 kn
    },
];

let tick = 0;

function ownVesselDelta() {
    const t = (tick * TICK_MS) / 1000;
    // Straight north from start. Latitude only moves; longitude stays put.
    const dNorthM = OWN_SOG_MS * t;
    const dLatDeg = (dNorthM / 1852.0) * NM_TO_LAT;
    const lat = OWN_LAT + dLatDeg;
    const lon = OWN_LON;
    // Static environment so depth + wind don't constantly re-trigger
    // shallow / wind-shift alarms while the helm watches the CPA chips.
    const values = [
        { path: 'navigation.position', value: { latitude: lat, longitude: lon } },
        { path: 'navigation.speedOverGround', value: OWN_SOG_MS },
        { path: 'navigation.courseOverGroundTrue', value: OWN_COG },
        { path: 'navigation.headingTrue', value: OWN_COG },
        { path: 'environment.depth.belowTransducer', value: 12.0 },
        { path: 'environment.wind.directionTrue', value: Math.PI },
        { path: 'environment.wind.speedTrue', value: 7.5 },
        { path: 'environment.wind.angleApparent', value: 0.6 },
        { path: 'environment.wind.speedApparent', value: 9.0 },
    ];
    // Publish design once a minute so OnaPlotter's DraftFromSignalK
    // populates without spamming the bus.
    if (tick % 60 === 0) {
        values.push({ path: 'design.draft.current', value: 1.8 });
        values.push({ path: 'design.draft.maximum', value: 2.0 });
    }
    return { context: 'vessels.self', updates: [{ timestamp: new Date().toISOString(), values }] };
}

function aisDeltas() {
    const t = (tick * TICK_MS) / 1000;
    return AIS_TARGETS.map(v => {
        // Walk each target forward at its own velocity. Initial position
        // is own's start + offset; subsequent positions add the per-target
        // velocity vector projected onto north / east axes.
        const tgtNorthNm = v.offsetNorthNm + (Math.cos(v.cog) * v.sogMs * t) / 1852.0;
        const tgtEastNm = v.offsetEastNm + (Math.sin(v.cog) * v.sogMs * t) / 1852.0;
        const lat = OWN_LAT + tgtNorthNm * NM_TO_LAT;
        const lon = OWN_LON + tgtEastNm * NM_TO_LON;
        return {
            context: `vessels.urn:mrn:imo:mmsi:${v.mmsi}`,
            updates: [{
                timestamp: new Date().toISOString(),
                values: [
                    { path: '', value: { name: v.name, mmsi: v.mmsi } },
                    { path: 'navigation.position', value: { latitude: lat, longitude: lon } },
                    { path: 'navigation.speedOverGround', value: v.sogMs },
                    { path: 'navigation.courseOverGroundTrue', value: v.cog },
                    { path: 'design.aisShipType', value: { id: v.shipType } },
                    { path: 'communication.callsignVhf', value: v.callsign },
                ]
            }]
        };
    });
}

// TCP server SK connects to via its providers/tcp pipedProvider. Same
// shape as the regular fake-data publisher.
const LISTEN_PORT = parseInt(process.env.FAKE_LISTEN_PORT || '10111', 10);
const clients = new Set();
const server = net.createServer((socket) => {
    clients.add(socket);
    socket.on('close', () => clients.delete(socket));
    socket.on('error', () => clients.delete(socket));
    console.log(`[fake-data-cpa] client connected (${clients.size} total)`);
});
server.listen(LISTEN_PORT, '0.0.0.0', () => {
    console.log(`[fake-data-cpa] listening on 0.0.0.0:${LISTEN_PORT}`);
});

function sendDelta(delta) {
    const line = JSON.stringify(delta) + '\n';
    for (const socket of clients) {
        try { socket.write(line); } catch { /* socket died */ }
    }
}

console.log(`[fake-data-cpa] starting (tick=${TICK_MS}ms, ${AIS_TARGETS.length} targets)`);
setInterval(() => {
    tick++;
    sendDelta(ownVesselDelta());
    for (const d of aisDeltas()) sendDelta(d);
}, TICK_MS);
