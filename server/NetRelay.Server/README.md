# NetRelay.Server

ASP.NET Core 8 后端。B1 当前实现管理认证基础、MySQL EF Core 模型、初始 Migration、健康检查和统一 API 基础设施。

## 本地配置

不要把真实 Secret 写入 `appsettings.json`。使用环境变量或本地未提交配置：

```powershell
$env:ConnectionStrings__NetRelay = "Server=localhost;Port=3306;Database=netrelay;User=netrelay;Password=..."
$env:NetRelay__PublicBaseUrl = "https://netrelay.example"
$env:NetRelay__GithubRepository = "owner/repository"
$env:NetRelay__AutoMigrate = "false"
$env:BootstrapAdmin__Username = "admin"
$env:BootstrapAdmin__Password = "..."
$env:BootstrapAdmin__TotpSecret = "BASE32..."
$env:DataProtection__KeysPath = "D:\secure\netrelay-data-protection"
```

数据库中没有管理员时，三个 `BootstrapAdmin` 值为必填。初始化后应从部署环境移除管理员密码和 TOTP Secret，再重启服务。`DataProtection:KeysPath` 必须是持久化绝对路径，否则服务拒绝启动，避免重启后无法解密 TOTP Secret。

## 命令

```powershell
dotnet tool restore
dotnet build NetRelay.sln -c Release
dotnet run --project server/NetRelay.Server.Tests -c Release
dotnet tool run dotnet-ef migrations add <Name> --project server/NetRelay.Server --startup-project server/NetRelay.Server --output-dir Data/Migrations
dotnet run --project server/NetRelay.Server -- --migrate
```

`/health/live` 不访问数据库；`/health/ready` 检查 MySQL 连通性。
