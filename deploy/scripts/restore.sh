#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <backup-directory>" >&2
  exit 2
fi

backup_dir="$(realpath "$1")"
cd "$(dirname "$0")/.."
(cd "${backup_dir}" && sha256sum --check SHA256SUMS)
docker compose --env-file .env exec -T mysql \
  sh -c 'exec mysql -u root -p"$MYSQL_ROOT_PASSWORD" "$MYSQL_DATABASE"' \
  <"${backup_dir}/netrelay.sql"
echo "Database restore completed."
