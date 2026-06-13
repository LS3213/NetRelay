# 后端 API 与共享契约规范

[上一篇：后端平台分阶段开发与验收计划](11-backend-staged-development-and-acceptance-plan.md) | [返回索引](README.md)

> 状态：B1 实现中。`NetRelay.Contracts` 与后端基础正在按本文建立；实现变化需同步更新本文和 OpenAPI。

OpenAPI 初稿见 [`openapi/netrelay-v1.yaml`](openapi/netrelay-v1.yaml)。该文件当前只定义公开客户端核心接口；管理 API 将在 B1 认证边界稳定后补全。

当前 B1 已实现：

- `NetRelay.Contracts` 中的协议头、错误码、统一响应和管理认证 DTO。
- 请求 ID 生成与回传、`X-NetRelay-Protocol: 1` 校验、统一异常响应。
- `/health/live` 与包含 MySQL 连通性的 `/health/ready`。
- `/api/v1/admin/auth/login`、`totp`、`me`、`reauthenticate` 和 `logout`。

尚未实现的公开客户端接口与其他管理业务接口仍以本文和 OpenAPI 作为设计，不得视为可调用接口。

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
| `GET /api/v1/updates/{version}/download/{assetId}` | 下载服务器主源更新包 |

更新清单载荷至少包含版本、通道、架构、最低升级版本、包大小、SHA256、签名、发布日期和发布说明摘要。主源不可用时客户端直接调用 GitHub Releases API，不经后端代理。

### 5.3 客户端部署配置

签名客户端配置至少包含：

```json
{
  "configurationVersion": 1,
  "primaryApiBaseUrl": "https://netrelay.example/api/v1",
  "githubRepository": "owner/repository",
  "updateChannel": "stable",
  "allowGithubFallback": true
}
```

- 客户端内置初始配置并保存最后有效签名配置。
- 发布版客户端必须在编译资源 `bootstrap-config.json` 中预置可用的主后端地址、GitHub 备用仓库和更新通道。
- Release 构建必须拒绝示例域名、空地址和占位 GitHub 仓库。
- 后端管理后台可修改并发布新配置。
- `primaryApiBaseUrl` 必须为 HTTPS。
- `githubRepository` 必须是规范化的 `owner/repository`，客户端自行构造 GitHub Releases API 地址。
- 配置版本必须严格递增；无效签名、版本回退、过期或不可用的新主地址不得覆盖最后有效配置。
- 后端不可用时不得为获取 GitHub 地址而再次依赖后端。

### 5.4 公告与反馈

| 方法与路径 | 用途 |
| --- | --- |
| `GET /api/v1/announcements?version=...` | 获取适用的签名公告 |
| `POST /api/v1/feedback` | 创建反馈元数据 |
| `POST /api/v1/feedback/{id}/attachments` | 用户明确同意后上传附件 |

附件使用流式上传，必须限制数量、单文件大小、总大小和 MIME 类型。创建反馈不会隐式创建附件上传。

## 6. 管理 API

所有管理端点位于 `/api/v1/admin`，要求认证、TOTP 已完成、CSRF 防护和审计。

| 分组 | 初步端点 |
| --- | --- |
| 认证 | `POST /auth/login`、`POST /auth/totp`、`POST /auth/logout`、`POST /auth/reauthenticate` |
| 设备 | `GET /devices`、`GET /devices/{id}` |
| 封锁 | `POST /device-blocks`、`POST /device-blocks/{id}/revoke` |
| 全局策略 | `GET /policies`、`POST /policies`、`POST /policies/{id}/revoke` |
| 发布 | `GET /releases`、`POST /releases`、`POST /releases/{id}/publish`、`POST /releases/{id}/revoke` |
| 客户端配置 | `GET /client-configuration`、`POST /client-configuration`、`POST /client-configuration/{id}/publish` |
| 公告 | `GET /announcements`、`POST /announcements`、`POST /announcements/{id}/revoke` |
| 反馈 | `GET /feedback`、`GET /feedback/{id}`、`GET /feedback/{id}/attachments/{attachmentId}` |
| 审计 | `GET /audit-logs` |

发布全局封锁、签名更新和轮换密钥属于敏感操作，要求短时重新认证和二次确认。

B1 当前认证实现边界：

- 密码验证成功只签发短时、受 Data Protection 保护且数据库一次性消费的 TOTP 挑战。
- TOTP 成功后签发 `Secure`、`HttpOnly`、`SameSite=Strict` 的管理会话 Cookie。
- CSRF Token 只在会话创建响应中返回，状态变更端点要求 `X-NetRelay-Csrf`。
- 会话令牌和 CSRF Token 在数据库中只保存 SHA-256 哈希。
- `reauthenticate` 更新短时重新认证窗口；`logout` 持久化撤销会话。
- 当前未启用 CORS，因此浏览器跨域请求默认不被授权；管理后台部署后仍保持同源。

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
- Release Asset 命名和新增更新通道策略。
- 附件大小、反馈频率和公开 API 具体限流值。
