#!/usr/bin/env bash
set -euo pipefail

service_name="netrelay"
package_root="__PACKAGE_ROOT__"
config_root="__CONFIG_ROOT__"
install_script="${package_root}/install.sh"

run_privileged() {
  if [[ "${EUID}" -eq 0 ]]; then
    "$@"
  else
    sudo "$@"
  fi
}

show_header() {
  echo
  echo "=============================="
  echo " NetRelay 服务器维护菜单"
  echo " 安装目录: ${package_root}"
  echo " 配置目录: ${config_root}"
  echo "=============================="
}

show_menu() {
  show_header
  echo "1) 查看后端运行状态"
  echo "2) 查看最近 200 行服务日志"
  echo "3) 启动服务"
  echo "4) 停止服务"
  echo "5) 重启服务"
  echo "6) 执行更新/重新安装脚本"
  echo "7) 显示部署路径信息"
  echo "0) 退出"
  echo
}

show_paths() {
  echo "服务名: ${service_name}"
  echo "安装目录: ${package_root}"
  echo "安装脚本: ${install_script}"
  echo "配置目录: ${config_root}"
  echo "systemd 单元: /etc/systemd/system/${service_name}.service"
}

run_choice() {
  local choice="${1:-}"
  case "${choice}" in
    1|status)
      run_privileged systemctl status "${service_name}" --no-pager
      ;;
    2|logs)
      run_privileged journalctl -u "${service_name}" -n 200 --no-pager
      ;;
    3|start)
      run_privileged systemctl start "${service_name}"
      run_privileged systemctl status "${service_name}" --no-pager
      ;;
    4|stop)
      run_privileged systemctl stop "${service_name}"
      echo "已停止 ${service_name}。"
      ;;
    5|restart)
      run_privileged systemctl restart "${service_name}"
      run_privileged systemctl status "${service_name}" --no-pager
      ;;
    6|install|update|reinstall)
      if [[ ! -f "${install_script}" ]]; then
        echo "找不到安装脚本: ${install_script}" >&2
        exit 1
      fi
      run_privileged bash "${install_script}"
      ;;
    7|paths|info)
      show_paths
      ;;
    0|exit|quit)
      exit 0
      ;;
    *)
      echo "无效选项: ${choice}" >&2
      return 1
      ;;
  esac
}

if [[ $# -gt 0 ]]; then
  run_choice "$1"
  exit $?
fi

while true; do
  show_menu
  read -r -p "请输入数字: " choice
  echo
  if [[ "${choice}" == "0" ]]; then
    exit 0
  fi

  if ! run_choice "${choice}"; then
    echo
    read -r -p "按回车继续..." _
    continue
  fi

  echo
  read -r -p "操作完成，按回车返回菜单..." _
done
