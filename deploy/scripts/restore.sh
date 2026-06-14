#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "使用方法: $0 <备份目录>" >&2
  exit 2
fi

backup_dir="$(realpath "$1")"
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

# 检查备份完整性
if [ ! -f "${backup_dir}/netrelay.sql" ] || [ ! -f "${backup_dir}/SHA256SUMS" ]; then
  echo "错误：指定的备份目录中缺少必要的文件 (netrelay.sql 或 SHA256SUMS)。" >&2
  exit 1
fi

echo "=== 开始数据库恢复流程 ==="

echo "正在验证备份文件的 SHA256 校验和..."
(cd "${backup_dir}" && sha256sum --check SHA256SUMS)

# 检查数据库容器是否在运行
state="$(docker compose --env-file .env ps mysql --format "{{.State}}")"
if [ "$state" != "running" ]; then
  echo "错误：数据库服务未在运行中，无法恢复。请先启动服务。" >&2
  exit 1
fi

echo "正在将备份数据导入数据库..."
docker compose --env-file .env exec -T mysql \
  sh -c 'exec mysql -u root -p"$MYSQL_ROOT_PASSWORD" "$MYSQL_DATABASE"' \
  <"${backup_dir}/netrelay.sql"

echo "数据库恢复成功完成。"
