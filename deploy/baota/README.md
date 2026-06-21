# NetRelay 宝塔便携部署

该部署方式不使用 Docker，不要求服务器安装 Node.js 或 .NET Runtime。发布包携带 Linux x64 自包含后端、官网和已经构建的管理后台。

## 构建发布包

在 Windows 开发机仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File deploy/windows/build-delivery.ps1
powershell -ExecutionPolicy Bypass -File deploy/baota/build-package.ps1
```

必须先生成 Windows 正式交付产物。宝塔打包脚本会把 `artifacts/delivery/win-x64/installer/NetRelaySetup.exe` 自动复制为 `public/downloads/NetRelaySetup.exe`，官网的所有“下载 Windows 版”按钮直接下载该文件。若安装器不存在，脚本会拒绝生成便携包。

输出：

```text
artifacts/baota-portable.zip
```

上传服务器前，建议先确认 `artifacts/baota-portable/` 根目录同时存在：

- `install.sh`
- `netrelay-menu.sh`
- `netrelay.service`
- `nginx-location.conf`
- `README.md`
- `app/`
- `public/`
- `public/downloads/NetRelaySetup.exe`

## 宝塔安装

1. 在宝塔创建 MySQL 数据库和专用账号。
2. 将 ZIP 上传并解压到 `/www/wwwroot/netrelay`。
3. SSH 执行：

```bash
cd /www/wwwroot/netrelay
sudo bash install.sh
```

升级现有环境时，必须用新的 `baota-portable.zip` 整包覆盖部署目录，再执行 `install.sh`。不要只替换 `app/`，否则会遗漏 `install.sh`、`netrelay-menu.sh` 和服务模板更新。

4. 记录脚本输出的一次性安装令牌。
5. 在宝塔创建纯静态网站，网站目录设置为 `/www/wwwroot/netrelay/public`。
6. 将 `nginx-location.conf` 内容加入该网站配置的 `server` 块。
7. 在宝塔申请并强制启用 HTTPS。
8. 访问 `https://域名/install/`，输入令牌和安装配置。

安装页的 `/install/` 路由必须保留 `location ^~ /install/` 写法。`^~` 用于阻止宝塔默认的 CSS/JavaScript 静态文件正则规则截获安装页资源；缺失时安装页可能显示为无样式的原始 HTML。

安装完成后：

- 安装向导直接迁移数据库并创建唯一管理员。
- `runtime-config.json` 和 `installed.lock` 写入 `/www/server/netrelay/config`，不位于网站公开目录。
- 磁盘中的一次性 `install.token` 被删除；安装锁存在时，即使运行配置损坏也不会重新开放安装器。
- 管理员密码与 TOTP Secret 不写入运行配置。
- systemd 自动重启后，安装 API 不再注册。
- 再次执行 `install.sh` 只会刷新 systemd 服务、执行数据库迁移并重启后端，不会重新安装或重新开放安装入口。
- 覆盖新便携包后再次执行 `install.sh` 会先运行 `./app/NetRelay.Server --migrate`，迁移成功后才重启正在运行的后端，使新版本代码和数据库结构保持一致。
- 安装脚本会自动注册全局维护命令：`NetRelay`、`netrelay`、`NR`。

若执行完 `install.sh` 后仍提示 `NR: command not found`，应先检查：

```bash
ls -l /www/wwwroot/netrelay/install.sh /www/wwwroot/netrelay/netrelay-menu.sh
ls -l /usr/local/bin/netrelay /usr/local/bin/NetRelay /usr/local/bin/NR
```

通常这意味着服务器上仍是旧便携包，或只更新了 `app/` 而没有更新根目录脚本。

如果要启用“后台发布/撤回时自动同步 GitHub”，请在安装完成后编辑 `/www/server/netrelay/config/runtime-config.json` 的 `netRelay` 段，至少补充：

- `githubSyncEnabled: true`
- `githubToken: "<你的 GitHub Token>"`
- `githubPagesBranch: "gh-pages"`（默认）
- `githubReleaseTagPrefix: "v"`（默认）
- `githubAssetName: "win-x64.zip"`（默认）

启用后，后台发布稳定版时，后端会自动更新 GitHub Release 资源和 `gh-pages/updates/stable/win-x64/latest.json`、`history.json`；若 GitHub 写入失败，发布或撤回会整体失败并回滚。

## 常用命令

```bash
systemctl status netrelay
journalctl -u netrelay -n 100 --no-pager
systemctl restart netrelay
```

## 全局维护菜单

安装完成后，可在任意目录直接输入以下任一命令：

```bash
NetRelay
netrelay
NR
```

命令会弹出数字菜单，当前内置操作包括：

1. 查看后端运行状态
2. 查看最近 200 行服务日志
3. 启动服务
4. 停止服务
5. 重启服务
6. 执行当前部署目录下的 `install.sh`（用于升级后的“更新安装”/重新执行 Migration 与刷新服务）
7. 显示安装目录、配置目录和脚本路径

也支持直接带参数：

```bash
netrelay status
netrelay logs
netrelay restart
netrelay update
```

## 安全边界

- `install.token`、运行配置和 Data Protection 密钥必须只允许服务账号读取。
- 安装前仅开放安装页面和数据库测试/安装接口，安装接口强制验证一次性令牌。
- 不要将 `config/` 或 `data/` 放入宝塔网站目录。
- 安装完成后不要删除 `installed.lock` 或 Data Protection 密钥。
- 如果通过 `NETRELAY_CONFIG_ROOT` 修改配置目录，还必须同步修改 `nginx-location.conf` 中安装锁的绝对路径。
- 如果在网页安装向导中使用自定义数据目录，必须先创建该目录，并确保 systemd 服务账号有读写权限。
- 宝塔 MySQL 应使用专用数据库和专用账号；远程数据库应额外强制 TLS。

## 依赖边界

| 环境 | 必需依赖 |
| --- | --- |
| Windows 构建机 | .NET 8 SDK、Node.js/npm、PowerShell、Git |
| Ubuntu/宝塔服务器 | Nginx、MySQL 8、systemd、OpenSSL |

服务器不需要安装 Node.js 或 .NET Runtime。管理后台已经构建为静态文件，后端为 Linux x64 自包含程序。

## 尚未完成的验收

该部署路径已完成代码和自动构建验证，但仍需在真实 Ubuntu、宝塔 Nginx 和 MySQL 8 环境验证首次安装、HTTPS 管理登录、备份与恢复。在这些项目完成前，不应将 B1 部署验收标记为通过。
