// Path of the logged-in storage state global-setup.js writes, shared
// with playwright.config.js. Set only when credentials are configured.

import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));

export const STORAGE_STATE_PATH = join(__dirname, '.auth', 'storage-state.json');

export const HAS_CREDENTIALS = !!(process.env.SK_USER && process.env.SK_PASSWORD);
