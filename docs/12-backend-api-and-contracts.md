# 后端 API 与共享契约规范

[上一篇：后端平台分阶段开发与验收计划](11-backend-staged-development-and-acceptance-plan.md) | [返回索引](README.md)

> 状态：B7 已通过。`NetRelay.Contracts` 与后端基础已全部实现；实现变化需同步更新本文和 OpenAPI。

OpenAPI 见 [`openapi/netrelay-v1.yaml`](openapi/netrelay-v1.yaml)。该文件当前定义了公开客户端核心接口及管理 API。

当前已实现：

- **B1 基础与认证**：
  - `NetRelay.Contracts` 中的协议头、错误码、统一响应和管理认证 DTO。
  - 请求 ID 生成与回传、`X-NetRelay-Protocol: 1` 校验、统一异常响应。
  - `/health/live` 与包含 MySQL 连通性的 `/health/ready`。
  - `/openapi/v1.yaml` 直接提供与仓库共同维护的运行时 OpenAPI。
  - `/api/v1/admin/auth/login`、`totp`、`me`、`csrf`、`reauthenticate` 和 `logout`。
  - `/api/v1/admin/audit/verify` 验证当前审计哈希链。
- **B2 设备激活与心跳**：
  - `/api/v1/devices/activate`（设备激活，指纹置信度匹配克隆）与 `/api/v1/devices/heartbeat`（活跃度心跳）。
  - `/api/v1/connectivity/challenge`（高可信联网验证挑战）。
- **B3 双源更新**：
  - `/api/v1/updates/latest`（获取最新清单）与 `/api/v1/updates/{version}/download/{filename}`（流式包下载）。
  - 管理端 `/api/v1/admin/releases`（更新包上传、草稿管理、双重 TOTP 授权发布与撤销、旧物理包清理）。
- **B4 反馈、日志与公告**：
  - `/api/v1/feedback`（反馈创建）、流式上传附件，管理端 `/api/v1/admin/feedback`（反馈列表、附件下载，受 Re-auth 保护）。
  - `/api/v1/announcements/active`（获取签名活动公告），管理端 `/api/v1/admin/announcements`（公告草稿、编辑、发布与撤销）。
- **B5 封锁与受限模式**：
  - `/api/v1/policies/evaluate`（设备及全局封锁策略评估，双重签名信封下发）。
  - 管理端 `/api/v1/admin/device-blocks` 与 `/api/v1/admin/policies`（封锁规则/全局限制的发布与撤销，带 CSRF/Re-auth 安全验证并计入 AuditLog）。
- **B7 反馈强化与设备库扩展（已通过）**：
  - `/api/v1/feedback` 继续使用 `multipart/form-data` 创建反馈，可携带匿名 `deviceId`、`installationId`、`clientVersion` 和 `osVersion`。
  - `/api/v1/feedback/my?deviceId=...` 允许客户端按本机匿名设备指纹查询自身反馈历史，仅返回标题、正文、状态和时间，不返回联系方式、附件下载链接或其他设备数据。
  - 管理端 `/api/v1/admin/devices` 返回已注册安装实例的匿名设备指纹、安装实例、版本、OS 和最近活跃时间，用于管理后台设备库和一键封锁联动。

## 1. 设计目标

- 客户端、更新器、服务器和管理后台使用统一协议定义。
- 公开 API 可版本化、可诊断、可限流并安全降级。
- 控制类数据除 HTTPS 外还必须经过应用层签名验证。
- 后端故障不得阻止已激活、未受限客户端使用本地功能。

## 2. 共享契约边界

计划创建 `src/NetRelay.Contracts/`，只包含：

- DTO、枚举、错误码和协议版本。
- 签名载荷结构、确定性序列化规则和验证接口。
- 更新清单、设备策略、公告和挑战响应。

禁止依赖 WPF、EF Core、ASP.NET Core、MySQL 或服务器私钥实现。数据库实体不得直接作为 API DTO 返回。

## 3. 通用协议

| 项目 | 规定 |
| --- | --- |
| 基础路径 | `/api/v1` |
| 传输 | HTTPS；生产拒绝明文 HTTP |
| 内容类型 | `application/json; charset=utf-8`，附件除外 |
| 时间 | RFC 3339 UTC，例如 `2026-06-13T00:00:00Z` |
| ID | 服务端资源使用 UUID v7；本地兼容资源可使用 UUID v4 |
| 枚举 | JSON 中使用稳定英文字符串 |
| 请求关联 | 客户端可发送 `X-Request-Id`，服务器总是返回关联 ID |
| 幂等 | 激活、反馈和管理发布操作使用 `Idempotency-Key` |
| 分页 | 游标分页，不以页码作为稳定同步依据 |
| 协议版本 | 请求头 `X-NetRelay-Protocol: 1` |
| 客户端版本 | 请求头 `X-NetRelay-Version` |

成功响应：

```json
{
  "requestId": "0197...",
  "data": {}
}
```

签名控制数据可直接返回完整签名信封，`204` 响应无正文；其他普通 JSON 成功响应使用上述包装结构。

错误响应：

```json
{
  "requestId": "0197...",
  "error": {
    "code": "DEVICE_ACTIVATION_REQUIRED",
    "message": "首次使用需要联网激活。",
    "retryable": true,
    "retryAfterSeconds": 30,
    "details": {}
  }
}
```

客户端不得依赖 `message` 判断逻辑，只依赖稳定错误码。

## 4. 签名信封

控制类响应统一使用：

```json
{
  "protocolVersion": 1,
  "keyId": "release-2026-01",
  "purpose": "update-manifest",
  "nonce": "Base64Url 随机值",
  "issuedAt": "2026-06-13T00:00:00Z",
  "expiresAt": "2026-06-13T00:05:00Z",
  "payload": {},
  "signature": "Base64Url 签名"
}
```

`purpose` 必须参与签名，防止公告被重解释为策略。正式确定性序列化采用 RFC 8785 JSON Canonicalization Scheme；B0 原型只验证了“排序键并签名固定字节”的可行性，不可直接作为正式实现。

## 5. 公开客户端 API

### 5.1 激活与心跳

| 方法与路径 | 用途 | 关键约束 |
| --- | --- | --- |
| `POST /api/v1/devices/activate` | 首次激活与协议同意 | 未同意协议不得调用；返回签名激活凭证 |
| `POST /api/v1/devices/heartbeat` | 匿名活跃度 | 正常每安装实例 24 小时最多一次 |
| `POST /api/v1/policies/evaluate` | 检查设备、版本和全局策略 | 返回签名策略；后端失败不等于封锁 |
| `POST /api/v1/connectivity/challenge` | 指定网卡高可信联网验证 | nonce 必须原样返回并签名 |
| `GET /api/v1/client-configuration` | 获取可迁移主 API 与 GitHub 备用源配置 | 返回签名配置；无效配置不得覆盖最后有效配置 |

激活请求不包含原始硬件序列号：

```json
{
  "installationId": "UUID",
  "fingerprintVersion": 1,
  "deviceId": "SHA256",
  "evidence": {
    "system": ["SHA256"],
    "motherboard": ["SHA256"],
    "cpu": ["SHA256"],
    "disk": ["SHA256"],
    "networkAdapterGuid": ["SHA256"],
    "networkMac": ["SHA256"],
    "networkModel": ["SHA256"]
  },
  "acceptedTermsVersion": "1.0",
  "acceptedPrivacyVersion": "1.0"
}
```

网卡证据仅包含 NetRelay 分类为物理候选的本机接口，且必须在客户端完成独立哈希。IP、流量、链路、联网状态和用户自定义名称不属于设备身份 API。

首次激活成功后，后端返回签名激活凭证和匿名 `machineCode`。后续请求携带机器码仅用于引用设备记录，服务端仍需验证激活凭证和证据匹配；机器码不是密码或授权令牌。

### 5.2 更新

| 方法与路径 | 用途 |
| --- | --- |
| `GET /api/v1/updates/latest?channel=stable&architecture=win-x64&currentVersion=...` | 获取签名更新清单 |
| `GET /api/v1/updates/history?channel=stable&architecture=win-x64` | 获取当前固定稳定通道仍处于已发布状态的更新历史 |
| `GET /api/v1/updates/{version}/download/{filename}` | 下载服务器主源更新包 |
| `HEAD /api/v1/updates/{version}/download/{filename}` | 供已安装客户端在正式下载前探测主源更新包是否可用 |

更新清单载荷至少包含版本、通道、架构、最低升级版本、包大小、SHA256、签名、发布日期、发布说明摘要和 `isMandatory` 强制更新标志。当前公开更新固定为 `stable / win-x64`。主源不可用时客户端直接读取 GitHub Pages 上的 `updates/stable/win-x64/latest.json` 与 `history.json`，再从 GitHub Release 资源下载同一版本的 `win-x64.zip`，不经后端代理。

更新历史接口仅返回公开发布信息，不返回下载路径、SHA256、草稿或已撤回记录。客户端启动检查在无新版时静默；发现新版时展示更新日志确认窗口。关于页的手动检查复用同一窗口，关于页更新历史通过公开历史接口读取。

更新包 `GET` 与 `HEAD` 必须兼容已经交付的旧客户端，允许下载请求缺少协议版本头，但只返回已发布且文件名匹配的更新包；更新检查和其他 API 仍执行协议版本校验。管理端上传创建草稿，发布后才允许公开检查和下载；撤回后禁止公开下载。相同版本、通道和架构的记录一旦创建即不可覆盖，撤回后仍保留记录和审计历史，修复内容必须提升版本号重新发布。

### 5.3 客户端部署配置

签名客户端配置至少包含：

```json
{
  "configurationVersion": 1,
  "primaryApiBaseUrl": "https://netrelay.example/api/v1",
  "githubFallback": {
    "enabled": true,
    "repository": "owner/repository",
    "releaseTagPrefix": "v",
    "assetName": "win-x64.zip"
  }
}
```

- 客户端内置初始配置并保存最后有效签名配置。
- 发布版客户端必须在编译资源 `bootstrap-config.json` 中预置可用的主后端地址和 GitHub 备用配置。
- Release 构建必须拒绝示例域名、空地址和占位 GitHub 仓库。
- 后端管理后台可修改并发布新配置。
- `primaryApiBaseUrl` 必须为 HTTPS。
- `githubFallback.repository` 必须是规范化的 `owner/repository`；客户端按固定规则构造 GitHub Pages 元数据地址和 GitHub Release 资源下载地址。
- `githubFallback.assetName` 当前必须与服务端镜像输出一致，默认 `win-x64.zip`。
- 配置版本必须严格递增；无效签名、版本回退、过期或不可用的新主地址不得覆盖最后有效配置。
- 后端不可用时不得为获取 GitHub 地址而再次依赖后端。

### 5.4 公告与反馈

| 方法与路径 | 用途 |
| --- | --- |
| `GET /api/v1/announcements?version=...` | 获取适用的签名公告 |
| `POST /api/v1/feedback` | 创建反馈元数据 |
| `POST /api/v1/feedback/{id}/attachments` | 用户明确同意后上传附件 |
| `GET /api/v1/feedback/my?deviceId=...` | 查询当前匿名设备指纹对应的反馈历史 |

附件使用流式上传，必须限制数量、单文件大小、总大小和 MIME 类型。创建反馈不会隐式创建附件上传。

## 6. 管理 API

所有管理端点位于 `/api/v1/admin`，要求认证、TOTP 已完成、CSRF 防护和审计。

| 分组 | 初步端点 |
| --- | --- |
| 认证 | `POST /auth/login`、`POST /auth/totp`、`GET /auth/me`、`GET /auth/csrf`、`POST /auth/logout`、`POST /auth/reauthenticate` |
| 设备 | `GET /devices`、`GET /devices/{id}` |
| 封锁 | `POST /device-blocks`、`POST /device-blocks/{id}/revoke` |
| 全局策略 | `GET /policies`、`POST /policies`、`POST /policies/{id}/revoke` |
| 发布 | `GET /releases`、`POST /releases`、`POST /releases/{id}/publish`、`POST /releases/{id}/revoke`、`POST /releases/cleanup` |
| 客户端配置 | `GET /client-configuration`、`POST /client-configuration`、`POST /client-configuration/{id}/publish` |
| 公告 | `GET /announcements`、`POST /announcements`、`POST /announcements/{id}/revoke` |
| 反馈 | `GET /feedback`、`GET /feedback/{id}`、`GET /feedback/{id}/attachments/{attachmentId}` |
| 审计 | `GET /audit-logs` |

发布全局封锁、签名更新和轮换密钥属于敏感操作，要求短时重新认证和二次确认。

B1 当前认证实现边界：

- 密码验证成功只签发短时、受 Data Protection 保护且数据库一次性消费的 TOTP 挑战。
- TOTP 成功后签发 `Secure`、`HttpOnly`、`SameSite=Strict` 的管理会话 Cookie。
- CSRF Token 在会话创建响应中返回；已认证的同源管理端可通过 `GET /auth/csrf` 轮换并获取新 Token，旧 Token 立即失效；状态变更端点要求 `X-NetRelay-Csrf`。
- 会话令牌和 CSRF Token 在数据库中只保存 SHA-256 哈希。
- `reauthenticate` 更新短时重新认证窗口；`logout` 持久化撤销会话。
- 未知用户名、错误密码和锁定账号对外统一返回认证失败；未知用户名仍执行虚拟密码哈希以降低时序泄露。
- 当前未启用 CORS，因此浏览器跨域请求默认不被授权；管理后台部署后仍保持同源。
- B1 管理后台当前只调用认证、重新认证和审计链校验接口；设备、更新、公告、反馈与封锁页面尚未实现。

## 7. 初始错误码

| 错误码 | HTTP | 是否可重试 |
| --- | --- | --- |
| `REQUEST_INVALID` | 400 | 否 |
| `PROTOCOL_UNSUPPORTED` | 400 | 否 |
| `DEVICE_ACTIVATION_REQUIRED` | 401 | 是 |
| `ACTIVATION_RECEIPT_INVALID` | 401 | 是 |
| `ADMIN_AUTHENTICATION_REQUIRED` | 401 | 否 |
| `ADMIN_TOTP_REQUIRED` | 401 | 否 |
| `ADMIN_REAUTHENTICATION_REQUIRED` | 403 | 否 |
| `RESOURCE_NOT_FOUND` | 404 | 否 |
| `IDEMPOTENCY_CONFLICT` | 409 | 否 |
| `RATE_LIMITED` | 429 | 是 |
| `ATTACHMENT_REJECTED` | 422 | 否 |
| `UPDATE_NOT_AVAILABLE` | 404 | 否 |
| `SERVICE_TEMPORARILY_UNAVAILABLE` | 503 | 是 |

签名验证失败主要发生在客户端本地，不应通过另一次不可信服务器响应决定是否忽略。

## 8. 兼容与弃用

- `/api/v1` 内只做向后兼容新增。
- 删除字段、改变语义或签名格式需要新 API 主版本。
- 客户端忽略未知非关键字段，缺失关键字段时拒绝对应控制数据。
- 后端至少支持当前正式版与上一正式版客户端；具体窗口在 B1 配置化。
- OpenAPI 是实现接口清单，本文是协议和安全边界；冲突时先停止发布并修正文档或实现。

## 9. B0 未确认项

- UUID v7 的具体库和 MySQL 映射方式。
- RFC 8785 实现库选择与跨语言测试向量。
- Release Asset 命名与未来是否重新引入多通道发布策略。
- 附件大小、反馈频率和公开 API 具体限流值。
