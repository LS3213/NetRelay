# 构建产物与更新流程规范

[返回索引](README.md)

本文是 NetRelay 构建、预览、部署和客户端更新的唯一操作基线。四类产物用途不同，不得互相替代、改名后冒充或手工重新压缩。

## 1. 四类标准产物

| 产物 | 唯一构建入口 | 标准输出 | 用途 |
| --- | --- | --- | --- |
| 客户端开发版本预览 | `deploy/windows/build-preview.ps1` | `artifacts/publish/win-x64/` | 当前开发阶段本机或受控测试机直接运行、检查 UI 和功能 |
| 客户端更新包 | `deploy/windows/build-delivery.ps1` | `artifacts/delivery/win-x64/win-x64.zip` | 上传管理后台“更新包文件”，供已安装客户端在线更新 |
| Windows 安装器 | `deploy/windows/build-delivery.ps1` | `artifacts/delivery/win-x64/installer/NetRelaySetup.exe` | 提供给首次安装或需要重新安装的 Windows 用户 |
| 后端宝塔便携包 | `deploy/baota/build-package.ps1` | `artifacts/baota-portable.zip` | 上传服务器并覆盖解压，用于首次部署或后端升级 |

所有产物都位于被 Git 忽略的 `artifacts/` 目录，不得提交二进制产物到仓库。

## 2. 通用前置检查

在仓库根目录执行构建。任何正式交付候选至少应先通过：

```powershell
dotnet build NetRelay.sln -c Release --no-restore
dotnet run --project tests\NetRelay.RegressionTests\NetRelay.RegressionTests.csproj -c Release --no-build --no-restore
dotnet run --project server\NetRelay.Server.Tests\NetRelay.Server.Tests.csproj -c Release --no-build --no-restore
npm.cmd --prefix website\admin run build
git diff --check
```

版本号纪律：

- 开发阶段生成预览、更新包、安装器或后端包时，均不得修改产品版本号。
- 只有维护者明确宣布进入发布阶段后，才允许评估版本号变更。
- 即使进入发布阶段，修改版本号前也必须再次获得维护者明确许可。
- 不得发布与当前已发布版本号相同但内容不同的在线更新包。

## 3. 客户端开发版本预览

构建命令：

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\build-preview.ps1
```

标准输出：

```text
artifacts/publish/win-x64/
├── NetRelay.exe
├── NetRelay.Updater.exe
├── Assets/
└── BUILD-INFO.txt
```

使用规则：

- 这是开发阶段预览目录，可直接运行 `artifacts/publish/win-x64/NetRelay.exe`。
- 主程序和独立更新器均为压缩单文件 `win-x64` 自包含产物，测试机无需预装 .NET Desktop Runtime。
- 每次构建脚本会先清理旧预览目录，避免混入旧 DLL。
- 标准目录根部只应保留主程序、独立更新器、`BUILD-INFO.txt` 和必要资源目录；不得重新引入展开式运行库散文件。
- `BUILD-INFO.txt` 记录产品版本、Commit、工作区是否脏、构建时间和关键文件 SHA256。
- 此目录不得上传到管理后台，不得提供给正式用户，也不得代替安装器。

## 4. 客户端更新包与安装器

统一构建命令：

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\build-delivery.ps1
```

如 Inno Setup 不在默认路径：

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\build-delivery.ps1 -InnoSetupPath "D:\Inno Setup 6\ISCC.exe"
```

标准输出：

```text
artifacts/delivery/win-x64/
├── publish/                       # 正式交付输入目录
├── win-x64.zip                    # 在线更新包
└── installer/NetRelaySetup.exe    # Windows 安装器
```

### 4.1 在线更新包

后台上传位置：管理后台 -> 更新发布 -> 上传更新包。

表单填写规范：

| 字段 | 填写要求 |
| --- | --- |
| 版本号 | 必须与获准发布的客户端产品版本一致 |
| 更新通道 | 正式用户使用 `stable`，受控测试使用 `beta` |
| 平台架构 | `win-x64` |
| 最低可升级版本 | 允许直接在线升级的最低客户端版本 |
| 更新日志 | 清晰描述用户可感知变化和重要修复 |
| 更新包文件 | 只能上传 `artifacts/delivery/win-x64/win-x64.zip` |

禁止事项：

- 不得上传 `NetRelaySetup.exe` 作为更新包。
- 不得上传 `artifacts/publish/win-x64/` 中的文件。
- 不得手工重新压缩、增删或修改 `win-x64.zip`。
- 不得手工向 ZIP 放入 `manifest.json`；签名清单与 ZIP 哈希由更新链路独立处理。
- 上传草稿后必须核对版本、通道、架构、大小和 SHA256，再执行发布。

### 4.2 Windows 安装器

`NetRelaySetup.exe` 用于首次安装和重新安装，不上传到管理后台更新包表单。

安装器行为：

- 中文安装向导。
- 用户可选择安装路径、开机自启动和桌面快捷方式。
- 携带运行所需的 .NET 组件，目标电脑无需预装或联网下载 .NET Desktop Runtime。
- 安装目录根部只应包含 `NetRelay.exe`、`NetRelay.Updater.exe`、`BUILD-INFO.txt`、`Assets/`、卸载器及必要安装元数据；升级安装会清理旧版展开式运行库散文件和语言资源目录。
- 因 NetRelay 需要控制网卡，安装器和客户端强制要求管理员权限。
- 正常卸载时询问是否删除配置、日志、诊断报告和更新缓存；静默卸载默认保留用户数据。

正式公开提供安装器前，必须完成代码签名和干净 Windows 10/11 安装、启动、卸载验收。

## 5. 后端宝塔便携包

构建命令：

```powershell
powershell -ExecutionPolicy Bypass -File deploy\baota\build-package.ps1
```

标准输出：

```text
artifacts/baota-portable/
artifacts/baota-portable.zip
```

部署或升级步骤：

1. 备份数据库、运行配置、密钥和上传文件。
2. 将 `artifacts/baota-portable.zip` 上传服务器。
3. 覆盖解压到 `/www/wwwroot/netrelay`，不得覆盖服务器外置的运行配置和数据目录。
4. SSH 执行：

```bash
cd /www/wwwroot/netrelay
sudo bash install.sh
```

5. `install.sh` 在已有安装上会停止服务、自动执行数据库 Migration，然后重启服务。
6. 检查：

```bash
systemctl status netrelay
journalctl -u netrelay -n 100 --no-pager
```

7. 验证健康端点、管理后台登录、客户端 API 和数据库迁移状态。

不得只替换服务器可执行文件而跳过 `install.sh`，否则可能造成代码与数据库 Schema 不一致。

首次安装、目录权限、Nginx 配置和安装锁细节见 [宝塔便携部署与首次安装向导](18-baota-portable-deployment.md)。

## 6. 标准更新流程

### 6.1 开发阶段预览

1. 完成功能开发与自动测试。
2. 保持版本号不变。
3. 运行 `build-preview.ps1`。
4. 从 `artifacts/publish/win-x64/NetRelay.exe` 进行客户端手工验收。
5. 发现问题后继续开发，不上传在线更新，不对外发布安装器。

### 6.2 客户端正式发布

1. 维护者明确宣布进入发布阶段。
2. 再次取得维护者对版本号修改的明确许可。
3. 修改并核对产品版本号。
4. 运行完整质量门槛。
5. 运行 `build-delivery.ps1`。
6. 核对 `BUILD-INFO.txt`、Git Commit、Dirty 状态、SHA256 和签名状态。
7. 上传 `win-x64.zip` 为更新草稿并核对元数据。
8. 在 `beta` 或受控设备完成真实更新、签名拒绝、哈希拒绝和回滚测试。
9. 发布更新，并向首次安装用户提供同一构建批次的 `NetRelaySetup.exe`。

当前服务端仍使用在线操作密钥动态签名 `update-manifest`，尚未满足离线发布密钥规范。因此以上流程当前只允许用于 B8 开发联调和受控验收，在离线签名清单链路完成前不得进行正式公开发布。

### 6.3 后端升级

1. 运行服务端测试和管理后台构建。
2. 运行 `deploy/baota/build-package.ps1`。
3. 备份生产环境。
4. 上传并覆盖解压 `baota-portable.zip`。
5. 执行 `sudo bash install.sh`，不得跳过 Migration。
6. 检查日志、健康端点、管理后台与客户端 API。

## 7. 产物选择速查

| 需要做什么 | 使用什么 |
| --- | --- |
| 看最新客户端开发效果 | `artifacts/publish/win-x64/NetRelay.exe` |
| 给已安装客户端在线更新 | 上传 `artifacts/delivery/win-x64/win-x64.zip` |
| 给新用户安装 | 提供 `artifacts/delivery/win-x64/installer/NetRelaySetup.exe` |
| 更新服务器和管理后台 | 上传 `artifacts/baota-portable.zip`，然后执行 `install.sh` |
