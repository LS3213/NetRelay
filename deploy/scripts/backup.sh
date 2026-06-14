#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

# 自检：验证环境文件是否存在
if [ ! -f .env ]; then
  echo "错误：未找到 .env 配置文件！" >&2
  exit 1
fi

# 自检：验证 docker 命令是否可用
if ! command -v docker >/dev/null 2>&1; then
  echo "错误：未找到 docker 命令，请先安装 Docker 和 Docker Compose。" >&2
  exit 1
fi

# 检查数据库容器是否在运行
state="$(docker compose --env-file .env ps mysql --format "{{.State}}")"
if [ "$state" != "running" ]; then
  echo "错误：数据库服务未在运行中，无法进行备份。请先启动服务。" >&2
  exit 1
fi

echo "=== 开始数据库备份 ==="

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
backup_dir="${NETRELAY_BACKUP_ROOT:-./backups}/${timestamp}"
mkdir -p "${backup_dir}"

echo "正在导出数据库数据..."
docker compose --env-file .env exec -T mysql \
  sh -c 'exec mysqldump --single-transaction -u root -p"$MYSQL_ROOT_PASSWORD" "$MYSQL_DATABASE"' \
  >"${backup_dir}/netrelay.sql"

# 检查导出的备份文件是否有效
if [ ! -s "${backup_dir}/netrelay.sql" ]; then
  echo "错误：导出的备份文件为空，备份失败！" >&2
  rm -f "${backup_dir}/netrelay.sql"
  exit 1
fi

echo "正在保存当前容器配置..."
docker compose --env-file .env config >"${backup_dir}/compose.resolved.yml"

echo "正在计算备份文件哈希并写入 SHA256SUMS..."
(cd "${backup_dir}" && sha256sum netrelay.sql >SHA256SUMS)

echo "备份成功完成。备份存放于：${backup_dir}"
