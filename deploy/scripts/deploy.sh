#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
docker compose --env-file .env config >/dev/null
docker compose --env-file .env build --pull netrelay-api
docker compose --env-file .env up -d mysql
docker compose --env-file .env run --rm --no-deps netrelay-api --migrate
docker compose --env-file .env up -d
docker compose --env-file .env ps
