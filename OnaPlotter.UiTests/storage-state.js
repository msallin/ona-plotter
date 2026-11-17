// Path of the storage state global-setup.js writes, shared with
// playwright.config.js.

import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));

export const STORAGE_STATE_PATH = join(__dirname, '.auth', 'storage-state.json');
