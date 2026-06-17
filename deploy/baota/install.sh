#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "请使用 root 执行：sudo bash install.sh" >&2
  exit 1
fi

package_root="$(cd "$(dirname "$0")" && pwd)"
config_root="${NETRELAY_CONFIG_ROOT:-/www/server/netrelay/config}"
data_root="${NETRELAY_DATA_ROOT:-/www/server/netrelay/data}"
service_user="${NETRELAY_SERVICE_USER:-www}"
service_group="${NETRELAY_SERVICE_GROUP:-www}"
service_file="/etc/systemd/system/netrelay.service"
menu_script="/usr/local/bin/netrelay"

if ! id "${service_user}" >/dev/null 2>&1; then
  echo "找不到宝塔运行用户 ${service_user}，可通过 NETRELAY_SERVICE_USER 和 NETRELAY_SERVICE_GROUP 指定。" >&2
  exit 1
fi

mkdir -p "${config_root}" "${data_root}"
install_token_file="${config_root}/install.token"
install_lock_file="${config_root}/installed.lock"
if [[ ! -f "${install_lock_file}" && ! -f "${install_token_file}" ]]; then
  openssl rand -hex 32 >"${install_token_file}"
fi

chown -R "${service_user}:${service_group}" "${config_root}" "${data_root}"
chmod 700 "${config_root}" "${data_root}"
if [[ -f "${install_token_file}" ]]; then
  chmod 600 "${install_token_file}"
fi
chmod +x "${package_root}/app/NetRelay.Server"

sed \
  -e "s|__PACKAGE_ROOT__|${package_root}|g" \
  -e "s|__CONFIG_ROOT__|${config_root}|g" \
  -e "s|__SERVICE_USER__|${service_user}|g" \
  -e "s|__SERVICE_GROUP__|${service_group}|g" \
  "${package_root}/netrelay.service" >"${service_file}"

sed \
  -e "s|__PACKAGE_ROOT__|${package_root}|g" \
  -e "s|__CONFIG_ROOT__|${config_root}|g" \
  "${package_root}/netrelay-menu.sh" >"${menu_script}"
chmod 755 "${menu_script}"
ln -sfn "${menu_script}" /usr/local/bin/NetRelay
ln -sfn "${menu_script}" /usr/local/bin/NR

systemctl daemon-reload
systemctl enable netrelay.service

if [[ -f "${install_lock_file}" ]]; then
  if [[ ! -f "${config_root}/runtime-config.json" ]]; then
    echo "检测到安装锁，但缺少 ${config_root}/runtime-config.json，拒绝启动以避免重新开放安装入口。" >&2
    exit 1
  fi

  echo "检测到已安装状态，正在执行数据库迁移..."
  systemctl stop netrelay.service >/dev/null 2>&1 || true
  (
    cd "${package_root}/app"
    NETRELAY_CONFIG_DIR="${config_root}" NETRELAY_INSTALL_MODE=false ./NetRelay.Server --migrate
  )
  echo "数据库迁移完成。"
fi

systemctl restart netrelay.service

echo
echo "NetRelay 服务已启动。"
echo "全局维护命令已安装：NetRelay / netrelay / NR"
if [[ -f "${install_lock_file}" ]]; then
  echo "检测到已安装状态，安装入口保持关闭。本次已刷新 systemd 服务、完成数据库迁移并重启后端。"
else
  echo "一次性安装令牌：$(cat "${install_token_file}")"
  echo
  echo "下一步："
  echo "1. 在宝塔创建网站，并将网站根目录设置为 ${package_root}/public"
  echo "2. 将 ${package_root}/nginx-location.conf 的内容加入该网站配置"
  echo "3. 配置 HTTPS，然后访问 https://你的域名/install/"
fi
