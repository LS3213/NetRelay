# NetRelay Website

NetRelay 的零依赖静态官网，可部署到 GitHub Pages、Cloudflare Pages、Netlify、Nginx 或任意静态文件服务器。

## 本地预览

在仓库根目录运行：

```powershell
python -m http.server 4173 --directory website
```

然后访问：

```text
http://localhost:4173
```

也可以直接打开 `website/index.html`，但使用本地 HTTP 服务器更接近生产环境。

## 安装器下载

官网按钮固定下载同站点下的安装器：

```js
const downloadUrl = "downloads/NetRelaySetup.exe";
```

宝塔便携包构建脚本会从 `artifacts/delivery/win-x64/installer/NetRelaySetup.exe` 自动复制该文件。标准顺序是先运行 `deploy/windows/build-delivery.ps1`，再运行 `deploy/baota/build-package.ps1`。安装器二进制不会提交到 Git。

若单独部署 `website/` 到其他静态平台，需要自行把同一构建批次的 `NetRelaySetup.exe` 上传到 `website/downloads/`，或在部署阶段映射到同名路径。

## 部署

### GitHub Pages

在仓库设置中将 Pages 来源设置为 GitHub Actions，或将 `website/` 内容发布到 `gh-pages` 分支。

### Cloudflare Pages

- Build command：留空
- Build output directory：`website`

### Nginx

将 `website/` 中的全部文件复制至站点根目录。

## 替换产品展示图

首屏目前使用 HTML 与 CSS 构建应用界面示意图，因此在任何分辨率下都保持清晰。获得正式产品截图后，可以将 `.app-window` 替换为 `<img>`，或继续保留示意图作为动态产品展示。
