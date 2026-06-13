#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
backup_dir="${NETRELAY_BACKUP_ROOT:-./backups}/${timestamp}"
mkdir -p "${backup_dir}"

docker compose --env-file .env exec -T mysql \
  sh -c 'exec mysqldump --single-transaction -u root -p"$MYSQL_ROOT_PASSWORD" "$MYSQL_DATABASE"' \
  >"${backup_dir}/netrelay.sql"
docker compose --env-file .env config >"${backup_dir}/compose.resolved.yml"
(cd "${backup_dir}" && sha256sum netrelay.sql >SHA256SUMS)
echo "Backup created at ${backup_dir}"
