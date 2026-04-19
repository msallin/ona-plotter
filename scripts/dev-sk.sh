#!/usr/bin/env bash
# Dev SignalK server + fake data pump. Wraps docker compose so the
# repeated flag + path don't need to live in muscle memory.
#
# Usage:
#   scripts/dev-sk.sh up        # start SK + fake-data, background
#   scripts/dev-sk.sh down       # stop both
#   scripts/dev-sk.sh logs       # follow fake-data + sk logs
#   scripts/dev-sk.sh restart    # stop + up
#   scripts/dev-sk.sh status     # ps
#
# After `up`, the server is on http://localhost:3000. Point OnaPlotter
# at it by editing OnaPlotter/wwwroot/appsettings.json:
#   "SignalK": { "ServerUrl": "http://localhost:3000" }

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="$SCRIPT_DIR/../docker/docker-compose.dev.yml"

cmd="${1:-up}"

case "$cmd" in
    up)
        docker compose -f "$COMPOSE_FILE" up -d --build
        echo "SignalK server: http://localhost:3000"
        echo "Admin UI:       http://localhost:3000/admin/"
        ;;
    down)
        docker compose -f "$COMPOSE_FILE" down
        ;;
    logs)
        docker compose -f "$COMPOSE_FILE" logs -f
        ;;
    restart)
        docker compose -f "$COMPOSE_FILE" down
        docker compose -f "$COMPOSE_FILE" up -d --build
        ;;
    status)
        docker compose -f "$COMPOSE_FILE" ps
        ;;
    *)
        echo "Usage: $0 {up|down|logs|restart|status}"
        exit 1
        ;;
esac
