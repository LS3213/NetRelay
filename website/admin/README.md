# NetRelay Admin

React + TypeScript + Vite 管理后台。该应用只负责浏览器管理界面，不会进入 WPF 客户端。

构建环境要求 Node.js 22.12 或更高版本。

## 本地构建

```powershell
npm install
npm run build
```

生产构建输出到 `dist/`，由 Nginx 挂载到 `/admin/`。管理 API 使用同源 `/api/v1`，不启用跨域访问。

本地开发服务器可用：

```powershell
npm run dev
```

Vite 会将 `/api` 代理到 `http://localhost:8080`。后端管理会话 Cookie 标记为 `Secure`，因此完整登录流程必须通过 HTTPS 反向代理验证；HTTP 开发服务器主要用于页面开发。

## 当前功能

- 管理员密码和 TOTP 两阶段登录。
- 已登录会话恢复与 CSRF Token 轮换。
- 会话到期时间和重新认证窗口展示。
- 敏感操作重新认证。
- 审计哈希链完整性校验。
- 安全退出并持久化撤销会话。

设备、更新、公告、反馈和封锁管理将在后续阶段接入。
