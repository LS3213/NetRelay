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

echo "=== 开始数据库迁移 ==="

echo "正在确保数据库容器已启动..."
docker compose --env-file .env up -d mysql

# 等待 mysql 容器就绪
echo "正在等待数据库服务就绪..."
for i in {1..30}; do
  state="$(docker compose --env-file .env ps mysql --format "{{.State}}")"
  health="$(docker compose --env-file .env ps mysql --format "{{.Health}}")"
  if [ "$state" == "running" ] && { [ "$health" == "healthy" ] || [ -z "$health" ]; }; then
    echo "数据库已就绪。"
    break
  fi
  if [ "$i" -eq 30 ]; then
    echo "警告：数据库可能尚未完全启动，将尝试继续执行迁移。" >&2
  else
    echo "等待数据库启动中 ($i/30)..."
    sleep 1
  fi
done

echo "正在执行数据库迁移..."
docker compose --env-file .env run --rm --no-deps netrelay-api --migrate
echo "数据库迁移执行完毕。"
