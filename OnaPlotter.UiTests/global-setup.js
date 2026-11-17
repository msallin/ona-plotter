// Logs in to the SignalK server once and hands every test a browser
// storage state that already holds the session, the same localStorage
// keys TokenStore writes after a "Remember me" login. Without it a
// server with security on answers every write the tests trigger (MOB,
// waypoint moves, notifications) with 401.
//
// Runs only when SK_USER and SK_PASSWORD are set; SK_URL defaults to
// http://localhost:3000 (the dev stack from scripts/dev-sk.sh).

import { mkdir, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';
import { STORAGE_STATE_PATH } from './storage-state.js';

/** SignalK tokens in the dev stack live 1 d; stay well inside that. */
const TOKEN_LIFETIME_MS = 12 * 60 * 60 * 1000;

export default async function globalSetup(config) {
    const user = process.env.SK_USER;
    const password = process.env.SK_PASSWORD;
    if (!user || !password) return;

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

    const baseURL = config.projects[0]?.use?.baseURL;
    if (!baseURL) throw new Error('globalSetup needs use.baseURL to know the app origin');
    const expiresAt = new Date(Date.now() + TOKEN_LIFETIME_MS).toISOString();

    await mkdir(dirname(STORAGE_STATE_PATH), { recursive: true });
    await writeFile(STORAGE_STATE_PATH, JSON.stringify({
        cookies: [],
        origins: [{
            origin: new URL(baseURL).origin,
            localStorage: [
                { name: 'ona.auth.token.v1', value: token },
                { name: 'ona.auth.expiresAt.v1', value: expiresAt },
            ],
        }],
    }, null, 2));
}
