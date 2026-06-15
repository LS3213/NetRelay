# Windows 客户端交付

第八阶段使用 Inno Setup 6 构建 Windows x64 安装器，并使用同一个构建入口生成管理后台可上传的更新 ZIP。

开发阶段只需预览当前客户端时，使用：

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\build-preview.ps1
```

预览输出位于 `artifacts/publish/win-x64/`，不得上传管理后台或替代正式安装器。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\build-delivery.ps1
```

如果 Inno Setup 安装在非默认目录，可显式指定：

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\build-delivery.ps1 -InnoSetupPath "D:\Inno Setup 6\ISCC.exe"
```

未安装 Inno Setup 6 时，可先只生成统一发布目录和更新 ZIP：

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\build-delivery.ps1 -SkipInstaller
```

产物：

- `artifacts/delivery/win-x64/publish/`：安装器输入与绿色发布目录。
- `artifacts/delivery/win-x64/win-x64.zip`：管理后台“更新包文件”应上传的 ZIP。
- `artifacts/delivery/win-x64/installer/NetRelaySetup.exe`：Windows 安装器。

更新包上传时，管理后台中的版本号、通道、最低可升级版本和更新日志仍需人工确认。服务端会计算 ZIP 的 SHA256 和大小，并在客户端检查更新时生成签名清单。独立更新器使用下载到本机的签名清单 sidecar 再次验签并校验 ZIP，更新 ZIP 内不放置会导致包哈希自引用的 `manifest.json`。

## 安装与卸载

- 安装器检测 x64 `.NET 8 Desktop Runtime`；缺失时从微软官方下载并静默安装。
- 安装向导使用简体中文并始终显示安装路径选择页，默认目录为 `%ProgramFiles%\NetRelay`。
- 安装向导允许用户选择是否创建桌面快捷方式、是否登录 Windows 后自动启动 NetRelay。
- NetRelay 控制网络适配器，安装器和客户端均强制请求管理员权限；管理员权限不是可关闭选项。
- 卸载时清理 `NetRelay AutoStart` 任务计划项、Toast AppUserModelId 和 `netrelay://` 协议注册。
- 卸载时询问是否同时删除 `%APPDATA%\NetRelay` 与 `%LOCALAPPDATA%\NetRelay` 用户数据。
- 正常交互卸载必须由用户明确选择是否删除用户数据；静默卸载不显示弹窗并默认保留用户数据。
- 简体中文语言包保存在 `deploy/windows/Languages/ChineseSimplified.isl`，构建不依赖开发机 Inno Setup 安装目录中的可选语言文件。

安装器与程序当前均未进行代码签名，正式公开发布前仍需配置可信代码签名证书并完成 Windows 10/11 干净虚拟机验收。

四类标准产物及完整更新步骤见 `docs/19-build-artifacts-and-update-workflow.md`。
