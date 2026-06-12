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

## 配置下载地址

在 `script.js` 中配置：

```js
const downloadUrl = "https://example.com/NetRelay.exe";
```

可以填写 GitHub Release、对象存储或其他正式下载地址。留空时，按钮会提示下载地址尚未配置。

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
