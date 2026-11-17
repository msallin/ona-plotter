// Writes the browser storage state every test starts from:
//   - the first-run hints (welcome card, long-press coachmark) marked
//     dismissed, as Map.FirstRun stores them, so no overlay appears
//     late and swallows clicks on a slow runner;
//   - when SK_USER and SK_PASSWORD are set, a SignalK session in the
//     localStorage keys TokenStore writes after a "Remember me" login.
//     Without it a server with security on answers every write the
//     tests trigger (MOB, waypoint moves, notifications) with 401.
//     SK_URL defaults to http://localhost:3000 (scripts/dev-sk.sh).

import { mkdir, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';
import { STORAGE_STATE_PATH } from './storage-state.js';

/** SignalK tokens in the dev stack live 1 d; stay well inside that. */
const TOKEN_LIFETIME_MS = 12 * 60 * 60 * 1000;

const DISMISSED_HINTS = [
    { name: 'ona.hints.welcome.v1.dismissed', value: '1' },
    { name: 'ona.hints.mapLongPress.v1.dismissed', value: '1' },
];

async function loginEntries() {
    const user = process.env.SK_USER;
    const password = process.env.SK_PASSWORD;
    if (!user || !password) return [];

    const skUrl = process.env.SK_URL ?? 'http://localhost:3000';
    const response = await fetch(new URL('/signalk/v1/auth/login', skUrl), {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ username: user, password }),
    });
    if (!response.ok) {
        throw new Error(`SignalK login at ${skUrl} failed: HTTP ${response.status}`);
    }
    const { token } = await response.json();
    if (!token) throw new Error(`SignalK login at ${skUrl} returned no token`);
    const expiresAt = new Date(Date.now() + TOKEN_LIFETIME_MS).toISOString();
    return [
        { name: 'ona.auth.token.v1', value: token },
        { name: 'ona.auth.expiresAt.v1', value: expiresAt },
    ];
}

export default async function globalSetup(config) {
    const baseURL = config.projects[0]?.use?.baseURL;
    if (!baseURL) throw new Error('globalSetup needs use.baseURL to know the app origin');

    await mkdir(dirname(STORAGE_STATE_PATH), { recursive: true });
    await writeFile(STORAGE_STATE_PATH, JSON.stringify({
        cookies: [],
        origins: [{
            origin: new URL(baseURL).origin,
            localStorage: [...DISMISSED_HINTS, ...await loginEntries()],
        }],
    }, null, 2));
}
