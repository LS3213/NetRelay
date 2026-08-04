# NetRelay 接入 Omnexa

本次实现以产品专属的[公开接入页](https://omnexa.lansil.cn/integration/app_YPDP33LMk4gw_Q3_s1kBqvAR/production)、
[AI Markdown 指南](https://omnexa.lansil.cn/api/v1/integration/app_YPDP33LMk4gw_Q3_s1kBqvAR/production/guide.md)
和[机器可读 JSON](https://omnexa.lansil.cn/api/v1/integration/app_YPDP33LMk4gw_Q3_s1kBqvAR/production)
为准。JSON `schemaVersion=1`，公告、设备指纹、备用分发、反馈、心跳、激活、离线
策略和发布共 8 项能力均已启用。服务端能力键 `installation` 是兼容名称，表示设备
激活与内部运行槽位，不代表后台存在“安装实例”管理功能。

## 1. 当前接入坐标

NetRelay 桌面客户端已接入 Omnexa 正式环境。唯一可信的产品参数定义在
`src/NetRelay.Contracts/OmnexaProduct.cs`：

| 参数 | 值 |
| --- | --- |
| 服务地址 | `https://omnexa.lansil.cn/` |
| API ID | `app_YPDP33LMk4gw_Q3_s1kBqvAR` |
| 产品键 | `netrelay` |
| 环境键 | `production` |
| 渠道 | `stable` |
| 平台 / 架构 | `windows` / `x64` |

API ID 和离线根公钥都是公开寻址/验签材料，不是客户端密钥。客户端不得携带
Omnexa 管理员凭据，也不得调用 `/admin/v1`。

### 1.1 旧 NetRelay 接口隔离

生产客户端不包含、也不会默认访问 `netrelay.lansil.cn`。激活、云控、心跳、公告、
更新、反馈和连接挑战均经 `OmnexaIntegrationService` 与 Omnexa SDK 完成；其默认
地址只能来自上表的 `OmnexaProduct.BaseAddress`。旧 `server/NetRelay.Server` 源码、
部署脚本和历史 DTO 仅为迁移参考，不是桌面客户端的生产依赖，也不被
`deploy/windows/build-delivery.ps1` 打入安装器或在线更新 ZIP。

运行时配置中的 `primaryApiBaseUrl` 和嵌入的 `bootstrap-config.json` 同样都指向
`https://omnexa.lansil.cn`，不用于保留旧站点地址。

## 2. 启动与后台生命周期

1. `ConfigurationService` 以规范化程序目录的 SHA-256 为键，在
   `%APPDATA%\NetRelay\config.json` 的 `runtimeSlotIds` 中保存稳定 UUID。线上协议
   字段仍名为 `installationId`，但实际语义是不可见的内部运行槽位。
2. 用户接受隐私与条款后，`ActivationService` 通过官方
   `WindowsFingerprintProvider` 提交已域哈希的设备证据。
3. 激活回执、操作证书和签名控制快照按槽位写入
   `%LOCALAPPDATA%\NetRelay\omnexa\production\{runtimeSlotId}`，不同程序目录不得
   共用缓存。
4. 主窗口启动前必须完成一次 `sync`。SDK 按“主 API → 签名备用源 →
   最后有效签名缓存”回退；三者均不可用且没有可用缓存时不进入正常模式。
5. `OmnexaControlRuntime` 按 `nextSyncSeconds` 加入 ±10% 抖动后同步策略；
   心跳按 `nextHeartbeatSeconds` 的本地计划发送。
6. 回执失效或服务端返回 `INSTALLATION_REQUIRED` /
   `ACTIVATION_RECEIPT_INVALID` 时，只重新激活一次，禁止无限重试。

同一程序目录从 1.0 升级到 2.0 时保留槽位 UUID，服务端只保留该槽位最新组合，
因此设备页只显示 2.0。不同程序目录使用不同槽位；即使两个版本只是交替启动，只要
各自最近在线仍处于环境离线宽限期内，设备页就同时显示两个活跃组合。后台只管理和
封禁设备，不显示或封禁运行槽位。

旧配置的单一 `installationId` 会在 schema v6 迁移时归入当前程序目录的运行槽位并
从配置中移除；schema v7 新增按客户端版本保存的公告展示历史。现有设备身份和激活
关联不会因此改变。正式环境与未来沙箱环境还必须
使用不同缓存目录，不能共享激活回执或控制快照。

## 3. 控制决策映射

| Omnexa 决策 | NetRelay 行为 |
| --- | --- |
| `allow` | 正常运行 |
| `restricted` | 禁止网卡切换和后台规则执行；仍允许检查更新与反馈 |
| `deny` | 阻止正常操作；仍允许更新到修复版本 |
| `maintenance=true` | 进入维护受限模式，等待后续同步恢复 |

若已验签 `release.isMandatory=true`，即使云控决策为 `allow`，客户端仍会阻止网卡
切换和自动化执行，并强制打开更新流程；用户关闭“启动时自动检查更新”不能绕过
必须更新。更新检查和下载在此状态下继续可用。

旧 NetRelay Server 的 `PersistedBlockState` 不再作为运行时信任来源。最后有效策略、
离线宽限、备用源 generation 回滚保护和主源安全下限均由 `Omnexa.Sdk` 的签名缓存
实现。

## 4. 更新信任链

更新信息来自已验证的 `client-sync` 快照。下载由 SDK 依次尝试 Omnexa 主源和清单
中的 GitHub 备用地址，并强制检查：

- 精确文件大小；
- SHA-256；
- 已签名快照中的 release ID、版本和目标平台。

下载完成后，客户端把原始 `SignedEnvelope`、操作证书和 release ID 交给独立更新器。
独立更新器退出主程序后再次使用内置离线根公钥验证：

1. 操作证书允许 `client-sync`；
2. 证书 `keyId` 与快照 `keyId` 一致；
3. P-256 / SHA-256 / P1363 快照签名有效且未过期；
4. 快照载荷中的 release ID 与待安装包一致；
5. 包大小和 SHA-256 再次匹配。

任一检查失败都不得写入安装目录。

## 5. 公告、反馈与连接诊断

- 公告直接使用同一份已验签 `sync` 快照，不再调用旧公告接口。
- 公告展示策略使用 Omnexa 规范值：`once` 在确认后跨版本永久跳过；
  `onceperversion` 按公告 ID + `Protocol.ProductVersion` 记录，每次升级到新版本只
  重显一次；`everylaunch` 每个进程启动显示一次，同一进程内不重复。旧
  `once_per_device`、`once_per_version`、`every_startup` 仅作为兼容别名。
- 严重程度与展示频率独立组合：`normal` 为普通提示，`important` 优先展示但继续
  运行，`critical` 最先展示并在用户确认后退出客户端。未知展示策略直接跳过，不能
  当作“每次启动”处理。
- 反馈先创建 JSON 会话，再用返回的一次性 `uploadToken` 上传日志 ZIP；历史读取
  必须携带当前运行槽位的激活回执，只能读取该槽位的最近记录。
- 诊断 ZIP 在客户端先限制为 10 MB，并固定使用 `.zip` +
  `application/zip`；服务端仍会执行扩展名与 MIME 双重白名单校验。
- 绑定指定网卡的连接挑战调用产品专属
  `/api/v1/client/{apiId}/{environment}/connectivity/challenge`，验证离线根证书链、
  `keyId`、purpose、nonce、有效期和签名。
- `IgnoreSslErrors` 不适用于 Omnexa。生产信任同时依赖 HTTPS 和离线签名链。

## 6. 本地覆盖与验证

只有开发测试允许用 `NETRELAY_OMNEXA_BASE_URL` 覆盖服务地址；HTTP 仅接受本机
环回地址。旧 `NETRELAY_BACKEND_URL` 仅作为回归测试兼容别名：它仍会被解释为
Omnexa 地址覆盖值，并非旧接口开关。生产设备不得设置这两个环境变量；尤其不得将
`NETRELAY_BACKEND_URL` 设置为 `https://netrelay.lansil.cn`，否则显式覆盖会改变
客户端请求目标。

标准验证命令：

```powershell
dotnet restore NetRelay.sln -p:NuGetAudit=false
dotnet build NetRelay.sln -c Release --no-restore -m:1
dotnet run --project tests\NetRelay.RegressionTests\NetRelay.RegressionTests.csproj `
  -c Release --no-build --no-restore
```

上线前还必须在 Windows 真机完成：首次激活、回执失效重激活、三种决策、维护模式、
主源断开、备用源断开、离线宽限到期、强制更新失败回滚和反馈附件白名单测试。

## 7. 2026-08-04 验证状态

- `NetRelay` 主客户端、`Omnexa.Core`、`Omnexa.Sdk` 和独立更新器已完成 Release 编译。
- Omnexa 自动测试 39/39 通过，包含 MySQL 活跃运行组合查询翻译与运行槽位迁移发现。
- NetRelay 回归测试 53/53 通过，包含目录槽位稳定性、不同目录隔离、旧
  `installationId` 配置迁移，以及三种严重程度与三种公告展示策略的全部组合。
- 开发预览版与正式版 `NetRelay.exe` 字节及 SHA-256 完全一致。
- 正式环境真实 `connectivity/challenge` 已到达 Omnexa，但返回
  HTTP 503 / `SERVICE_TEMPORARILY_UNAVAILABLE`：“在线操作证书尚未授权联网挑战
  用途，请先轮换操作密钥”。在 Omnexa 管理端轮换正式环境操作密钥并确认新证书
  允许 `connectivity-challenge` 前，不得把连接挑战标记为生产验收通过。
- 本次“设备 + 活跃运行组合”改造仍需在部署新 Omnexa 包后，用同目录升级和两个
  不同目录交替启动两组场景做生产验收；未部署前不得用旧后台结果判断新聚合逻辑。
- 源码、嵌入配置与 Windows 正式交付脚本已复核：不存在
  `netrelay.lansil.cn` 的默认地址或运行时调用路径。客户端只会在开发者显式设置
  `NETRELAY_BACKEND_URL` / `NETRELAY_OMNEXA_BASE_URL` 时改写 Omnexa 默认地址。
