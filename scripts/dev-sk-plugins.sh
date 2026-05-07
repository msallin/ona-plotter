#!/usr/bin/env bash
# One-time installer for the SK plugins OnaPlotter exercises in the
# dev instance:
#
#   * signalk-anchoralarm-plugin (sbender9, v2.0.0+) - target of the
#     v2 anchor flow rework. Without it OnaPlotter falls through to
#     the manual JS-only flow on every drop.
#   * signalk-tides-api - supplies environment.tide.* paths so the
#     tide HUD + anchor-tide alarm can leave their dormant state.
#
# Plugins land in the sk-data named volume so they persist across
# docker-compose restarts. Re-running this script is idempotent
# (npm install --no-save updates in place when versions change).
#
# Run AFTER `scripts/dev-sk.sh up` (the SK container has to be up
# so we can `docker exec` into it). The script:
#   1. Fixes the node_modules ownership so the plugin's npm install
#      doesn't fail on the SK container's default root-owned dir.
#   2. Runs `npm install` from inside the container to land the
#      packages in /home/node/.signalk/node_modules.
#   3. Writes plugin-config-data/<id>.json with enabled=true so the
#      plugins activate on the next SK restart without the helm
#      having to click through the admin UI.
#   4. Restarts the SK container so it discovers + activates them.
#
# Verify after running: log into http://localhost:3000/admin/ as
# admin / admin (dev creds in security.json). Webapps page should
# show OnaPlotter; Plugin Config page should show both anchoralarm
# + tides-api enabled.
set -euo pipefail

CONTAINER=${CONTAINER:-ona-sk-dev}
PLUGINS=("signalk-anchoralarm-plugin" "signalk-tides-api")

echo "==> Fixing node_modules ownership in $CONTAINER..."
docker exec --user 0 "$CONTAINER" \
    chown -R node:node /home/node/.signalk/node_modules

echo "==> Installing plugins (this may take ~30s the first time)..."
docker exec "$CONTAINER" sh -c \
    "cd /home/node/.signalk && npm install ${PLUGINS[*]} --no-audit --no-fund"

echo "==> Enabling both plugins via plugin-config-data..."
docker exec "$CONTAINER" sh -c \
    'echo "{\"enabled\": true, \"configuration\": {}}" > /home/node/.signalk/plugin-config-data/anchoralarm.json'
docker exec "$CONTAINER" sh -c \
    'echo "{\"enabled\": true, \"configuration\": {}}" > /home/node/.signalk/plugin-config-data/tides-api.json'

echo "==> Restarting $CONTAINER so plugins activate..."
docker restart "$CONTAINER" >/dev/null

# Wait for SK to come back up so the helm can hit the URL right away.
echo -n "==> Waiting for SK to come up "
until curl -fsS http://localhost:3000/signalk >/dev/null 2>&1; do
    echo -n "."
    sleep 2
done
echo " ready."

echo
echo "Plugins installed + enabled:"
echo "  - signalk-anchoralarm-plugin v2.0.0+ (PUT navigation.anchor.position works)"
echo "  - signalk-tides-api (set the station via http://localhost:3000/admin/)"
echo
echo "Open OnaPlotter:        http://localhost:3000/signalk-onaplotter/"
echo "Open SK admin UI:       http://localhost:3000/admin/  (admin / admin)"
