#!/usr/bin/env bash
# Render PNG icons from OnaPlotter/wwwroot/favicon.svg for the iPad
# / iOS PWA install path. iPadOS rejects SVG apple-touch-icon (since
# at least iOS 18) and the home-screen tile silently falls back to a
# rendered screenshot of the page. Same for the manifest's "icons"
# list when the OS picks one for the splash screen.
#
# Generates:
#   apple-touch-icon-180.png    (apple-touch-icon, retina iPad)
#   icon-192.png                (PWA manifest, common Android size)
#   icon-512.png                (PWA manifest, splash screen)
#
# Uses Playwright (already a dev dep for the UI tests) to rasterise
# the SVG. The .mjs lives next to the UiTests package so its
# `import 'playwright'` resolves against the existing node_modules
# without needing a top-level npm install.
#
# Run after editing favicon.svg:
#   ./scripts/generate-pwa-icons.sh
# Then bump service-worker.js CACHE_NAME so existing PWA installs
# refresh the cached PNGs.

set -euo pipefail

cd "$(dirname "$0")/.."

if ! command -v node >/dev/null 2>&1; then
    echo "ERROR: node not found on PATH" >&2
    exit 1
fi

if [[ ! -d OnaPlotter.UiTests/node_modules/playwright ]]; then
    echo "Playwright not installed in OnaPlotter.UiTests; running npm ci..."
    (cd OnaPlotter.UiTests && npm ci --silent)
fi

node OnaPlotter.UiTests/generate-pwa-icons.mjs
