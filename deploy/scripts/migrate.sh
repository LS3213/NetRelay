#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
docker compose --env-file .env up -d mysql
docker compose --env-file .env run --rm --no-deps netrelay-api --migrate
