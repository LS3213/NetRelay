#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

# 1. 自检：验证环境文件是否存在
if [ ! -f .env ]; then
  echo "错误：未找到 .env 配置文件！" >&2
  echo "请从 .env.example 复制并配置正确的生产环境变量。" >&2
  exit 1
fi

# 2. 自检：验证 docker 命令是否可用
if ! command -v docker >/dev/null 2>&1; then
  echo "错误：未找到 docker 命令，请先安装 Docker 和 Docker Compose。" >&2
  exit 1
fi

echo "=== 开始生产部署准备 ==="

# 3. 构建管理后台前端
echo "正在构建管理后台前端..."
npm --prefix ../website/admin ci
npm --prefix ../website/admin run build

# 4. 验证 docker compose 配置
echo "正在验证 docker compose 配置..."
docker compose --env-file .env config >/dev/null

# 5. 编译与拉取 API 镜像
echo "正在构建或更新 netrelay-api 镜像..."
docker compose --env-file .env build --pull netrelay-api

# 6. 启动 mysql 数据库
echo "正在启动数据库容器..."
docker compose --env-file .env up -d mysql

# 7. 执行数据库自动迁移
echo "正在执行数据库迁移..."
docker compose --env-file .env run --rm --no-deps netrelay-api --migrate

# 8. 启动所有剩余服务
echo "正在启动所有服务容器..."
docker compose --env-file .env up -d

# 9. 显示当前容器运行状态
echo "部署完成。容器状态如下："
docker compose --env-file .env ps
