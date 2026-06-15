using System.Threading.RateLimiting;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetRelay.Contracts;
using NetRelay.Server.Configuration;
using NetRelay.Server.Data;
using NetRelay.Server.Infrastructure;
using NetRelay.Server.Installation;
using NetRelay.Server.Security;
using NetRelay.Server.Services;

var builder = WebApplication.CreateBuilder(args);
var installationState = new InstallationState();
if (installationState.IsInstalled)
{
    builder.Configuration.AddJsonFile(installationState.RuntimeConfigPath, optional: false, reloadOnChange: false);
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
});

if (installationState.IsInstallMode)
{
    _ = installationState.ReadInstallToken();
    builder.Services.AddSingleton(installationState);
    builder.Services.AddSingleton<InstallationService>();
    var installApp = builder.Build();
    installApp.MapInstallationEndpoints();
    await installApp.RunAsync();
    return;
}

builder.Services.AddOptions<ServerOptions>()
    .Bind(builder.Configuration.GetSection(ServerOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => ServerOptionsValidator.Validate(options) is null, "Invalid NetRelay server configuration.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("NetRelay");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:NetRelay is required.");
}
builder.Services.AddDbContext<NetRelayDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0))));

var dataProtectionPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(dataProtectionPath) || !Path.IsPathFullyQualified(dataProtectionPath))
{
    throw new InvalidOperationException("DataProtection:KeysPath must be an absolute persistent directory.");
}

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("NetRelay.Server");
dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));

builder.Services.AddHealthChecks().AddCheck<MySqlHealthCheck>("mysql");
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        await ApiInfrastructure.Error(
            context.HttpContext,
            StatusCodes.Status429TooManyRequests,
            ErrorCodes.RateLimited,
            "请求过于频繁。",
            retryable: true,
            retryAfterSeconds: 300).ExecuteAsync(context.HttpContext);
    };
    options.AddPolicy("admin-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0
            }));
});
builder.Services.AddSingleton<AdminPasswordService>();
builder.Services.AddScoped<AdminAuthService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddSingleton<ManagedFileStorage>();
builder.Services.AddHostedService<StorageInitializationService>();
builder.Services.AddHostedService<AdminBootstrapService>();
builder.Services.AddSingleton<KeyManagementService>();
builder.Services.AddScoped<DeviceActivationService>();

var app = builder.Build();
app.UseForwardedHeaders();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseMiddleware<RequestIdentityMiddleware>();
app.UseRateLimiter();

app.MapGet("/", () => Results.Redirect("/health/live"));
app.MapHealthChecks("/health/live", new()
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready");
app.MapGet("/openapi/v1.yaml", async (CancellationToken cancellationToken) =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "OpenApi", "netrelay-v1.yaml");
    return Results.File(
        await File.ReadAllBytesAsync(path, cancellationToken),
        "application/yaml; charset=utf-8");
});

var adminAuth = app.MapGroup("/api/v1/admin/auth");
adminAuth.MapPost("/login", LoginAsync).RequireRateLimiting("admin-login");
adminAuth.MapPost("/totp", CompleteTotpAsync).RequireRateLimiting("admin-login");
adminAuth.MapGet("/me", GetIdentityAsync);
adminAuth.MapGet("/csrf", RotateCsrfAsync);
adminAuth.MapPost("/reauthenticate", ReauthenticateAsync).RequireRateLimiting("admin-login");
adminAuth.MapPost("/logout", LogoutAsync);
app.MapGet("/api/v1/admin/audit/verify", VerifyAuditAsync);

var adminReleases = app.MapGroup("/api/v1/admin/releases");
adminReleases.MapPost("/", CreateReleaseAsync);
adminReleases.MapGet("/", GetReleasesAsync);
adminReleases.MapPost("/{id}/publish", PublishReleaseAsync);
adminReleases.MapPost("/{id}/revoke", RevokeReleaseAsync);

var adminFeedback = app.MapGroup("/api/v1/admin/feedback");
adminFeedback.MapGet("/", GetFeedbacksAsync);
adminFeedback.MapPost("/{id}/status", UpdateFeedbackStatusAsync);
adminFeedback.MapGet("/{id}/attachment", DownloadFeedbackAttachmentAsync);

var adminDevices = app.MapGroup("/api/v1/admin/devices");
adminDevices.MapGet("/", GetRegisteredDevicesAsync);

var adminAnnouncements = app.MapGroup("/api/v1/admin/announcements");
adminAnnouncements.MapPost("/", CreateAnnouncementAsync);
adminAnnouncements.MapGet("/", GetAnnouncementsAsync);
adminAnnouncements.MapPut("/{id}", EditAnnouncementAsync);
adminAnnouncements.MapPost("/{id}/publish", PublishAnnouncementAsync);
adminAnnouncements.MapPost("/{id}/revoke", RevokeAnnouncementAsync);

var adminDeviceBlocks = app.MapGroup("/api/v1/admin/device-blocks");
adminDeviceBlocks.MapGet("/", GetDeviceBlocksAsync);
adminDeviceBlocks.MapPost("/", CreateDeviceBlockAsync);
adminDeviceBlocks.MapPost("/{id}/revoke", RevokeDeviceBlockAsync);

var adminPolicies = app.MapGroup("/api/v1/admin/policies");
adminPolicies.MapGet("/", GetGlobalPoliciesAsync);
adminPolicies.MapPost("/", CreateGlobalPolicyAsync);
adminPolicies.MapPost("/{id}/revoke", RevokeGlobalPolicyAsync);

var publicApi = app.MapGroup("/api/v1");
publicApi.MapPost("/devices/activate", ActivateDeviceAsync);
publicApi.MapPost("/devices/heartbeat", DeviceHeartbeatAsync);
publicApi.MapPost("/connectivity/challenge", GetConnectivityChallengeAsync);
publicApi.MapGet("/updates/latest", GetLatestUpdateAsync);
publicApi.MapGet("/updates/{version}/download/{filename}", DownloadUpdatePackageAsync);
publicApi.MapPost("/feedback", SubmitFeedbackAsync);
publicApi.MapGet("/feedback/my", GetMyFeedbacksAsync);
publicApi.MapGet("/announcements/active", GetActiveAnnouncementsAsync);
publicApi.MapPost("/policies/evaluate", EvaluatePolicyAsync);

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<NetRelayDbContext>();
    await dbContext.Database.MigrateAsync();
    return;
}

app.Run();

static async Task<IResult> LoginAsync(
    AdminLoginRequest request,
    HttpContext context,
    AdminAuthService authService,
    AuditService auditService,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(request.Username) ||
        request.Username.Length > 100 ||
        string.IsNullOrEmpty(request.Password) ||
        request.Password.Length > 1024)
    {
        return ApiInfrastructure.Error(
            context,
            StatusCodes.Status400BadRequest,
            ErrorCodes.RequestInvalid,
            "用户名和密码不能为空。");
    }

    var result = await authService.LoginAsync(
        request.Username.Trim(),
        request.Password,
        DateTimeOffset.UtcNow,
        cancellationToken);
    await auditService.WriteAsync(
        "admin.login.password",
        result.Success ? "success" : "failure",
        ApiInfrastructure.GetRequestId(context),
        details: new { username = request.Username.Trim() },
        cancellationToken: cancellationToken);

    return result.Success
        ? Results.Ok(new ApiResponse<AdminLoginChallenge>(ApiInfrastructure.GetRequestId(context), result.Challenge!))
        : ApiInfrastructure.Error(
            context,
            StatusCodes.Status401Unauthorized,
            result.ErrorCode!,
            "管理员认证失败。");
}

static async Task<IResult> CompleteTotpAsync(
    AdminTotpRequest request,
    HttpContext context,
    AdminAuthService authService,
    AuditService auditService,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(request.ChallengeToken) ||
        request.ChallengeToken.Length > 4096 ||
        string.IsNullOrWhiteSpace(request.Code) ||
        request.Code.Length != 6 ||
        !request.Code.All(char.IsAsciiDigit))
    {
        return ApiInfrastructure.Error(
            context,
            StatusCodes.Status400BadRequest,
            ErrorCodes.RequestInvalid,
            "TOTP 请求格式无效。");
    }

    var result = await authService.CompleteTotpAsync(
        request.ChallengeToken,
        request.Code,
        DateTimeOffset.UtcNow,
        cancellationToken);
    await auditService.WriteAsync(
        "admin.login.totp",
        result.Success ? "success" : "failure",
        ApiInfrastructure.GetRequestId(context),
        result.Session?.AdminAccountId,
        cancellationToken: cancellationToken);

    if (!result.Success)
    {
        return ApiInfrastructure.Error(
            context,
            StatusCodes.Status401Unauthorized,
            result.ErrorCode!,
            "TOTP 验证失败。");
    }

    context.Response.Cookies.Append(
        AdminAuthService.SessionCookieName,
        result.SessionToken!,
        CreateSessionCookie(result.Session!.ExpiresAt));
    return Results.Ok(
        new ApiResponse<AdminSessionResponse>(
            ApiInfrastructure.GetRequestId(context),
            new AdminSessionResponse(
                result.Session.AdminAccount!.Username,
                result.CsrfToken!,
                result.Session.ExpiresAt,
                result.Session.ReauthenticatedUntil)));
}

static async Task<IResult> GetIdentityAsync(
    HttpContext context,
    AdminAuthService authService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    return session is null
        ? ApiInfrastructure.Error(
            context,
            StatusCodes.Status401Unauthorized,
            ErrorCodes.AdminAuthenticationRequired,
            "需要管理员认证。")
        : Results.Ok(
            new ApiResponse<AdminIdentityResponse>(
                ApiInfrastructure.GetRequestId(context),
                new AdminIdentityResponse(
                    session.AdminAccount!.Username,
                    session.ExpiresAt,
                    session.ReauthenticatedUntil)));
}

static async Task<IResult> ReauthenticateAsync(
    AdminReauthenticateRequest request,
    HttpContext context,
    AdminAuthService authService,
    AuditService auditService,
    IOptions<ServerOptions> options,
    NetRelayDbContext dbContext,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024)
    {
        return ApiInfrastructure.Error(
            context,
            StatusCodes.Status400BadRequest,
            ErrorCodes.RequestInvalid,
            "密码格式无效。");
    }

    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(
            context,
            StatusCodes.Status401Unauthorized,
            ErrorCodes.AdminAuthenticationRequired,
            "需要管理员认证。");
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]) ||
        !authService.VerifyPassword(session, request.Password))
    {
        await auditService.WriteAsync(
            "admin.reauthenticate",
            "failure",
            ApiInfrastructure.GetRequestId(context),
            session.AdminAccountId,
            cancellationToken: cancellationToken);
        return ApiInfrastructure.Error(
            context,
            StatusCodes.Status403Forbidden,
            ErrorCodes.AdminReauthenticationRequired,
            "重新认证失败。");
    }

    session.ReauthenticatedUntil = DateTimeOffset.UtcNow.AddMinutes(options.Value.AdminReauthenticationMinutes);
    await dbContext.SaveChangesAsync(cancellationToken);
    await auditService.WriteAsync(
        "admin.reauthenticate",
        "success",
        ApiInfrastructure.GetRequestId(context),
        session.AdminAccountId,
        cancellationToken: cancellationToken);
    return Results.NoContent();
}

static async Task<IResult> RotateCsrfAsync(
    HttpContext context,
    AdminAuthService authService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(
            context,
            StatusCodes.Status401Unauthorized,
            ErrorCodes.AdminAuthenticationRequired,
            "需要管理员认证。");
    }

    var token = await authService.RotateCsrfAsync(session, cancellationToken);
    return Results.Ok(
        new ApiResponse<AdminCsrfResponse>(
            ApiInfrastructure.GetRequestId(context),
            new AdminCsrfResponse(token)));
}

static async Task<IResult> LogoutAsync(
    HttpContext context,
    AdminAuthService authService,
    AuditService auditService,
    NetRelayDbContext dbContext,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return Results.NoContent();
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]))
    {
        return ApiInfrastructure.Error(
            context,
            StatusCodes.Status403Forbidden,
            ErrorCodes.AdminCsrfInvalid,
            "CSRF 校验失败。");
    }

    session.RevokedAt = DateTimeOffset.UtcNow;
    await dbContext.SaveChangesAsync(cancellationToken);
    context.Response.Cookies.Delete(AdminAuthService.SessionCookieName, CreateSessionCookie(DateTimeOffset.UtcNow));
    await auditService.WriteAsync(
        "admin.logout",
        "success",
        ApiInfrastructure.GetRequestId(context),
        session.AdminAccountId,
        cancellationToken: cancellationToken);
    return Results.NoContent();
}

static async Task<IResult> VerifyAuditAsync(
    HttpContext context,
    AdminAuthService authService,
    AuditService auditService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(
            context,
            StatusCodes.Status401Unauthorized,
            ErrorCodes.AdminAuthenticationRequired,
            "需要管理员认证。");
    }

    var result = await auditService.VerifyChainAsync(cancellationToken);
    return Results.Ok(new ApiResponse<AuditChainVerificationResult>(ApiInfrastructure.GetRequestId(context), result));
}

static async Task<AdminSession?> ResolveRequiredSessionAsync(
    HttpContext context,
    AdminAuthService authService,
    CancellationToken cancellationToken)
{
    context.Request.Cookies.TryGetValue(AdminAuthService.SessionCookieName, out var sessionToken);
    return await authService.ResolveSessionAsync(sessionToken, DateTimeOffset.UtcNow, cancellationToken);
}

static CookieOptions CreateSessionCookie(DateTimeOffset expiresAt) => new()
{
    HttpOnly = true,
    Secure = true,
    SameSite = SameSiteMode.Strict,
    Path = "/",
    Expires = expiresAt,
    IsEssential = true
};

static async Task<IResult> ActivateDeviceAsync(
    DeviceActivationRequest request,
    HttpContext context,
    DeviceActivationService activationService,
    AuditService auditService,
    CancellationToken cancellationToken)
{
    if (request == null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "请求数据不能为空。");
    }

    try
    {
        var response = await activationService.ActivateDeviceAsync(request, cancellationToken);
        
        await auditService.WriteAsync(
            "device.activate",
            "success",
            ApiInfrastructure.GetRequestId(context),
            null,
            "device",
            request.InstallationId.ToString(),
            cancellationToken: cancellationToken);

        return Results.Ok(new ApiResponse<DeviceActivationResponse>(ApiInfrastructure.GetRequestId(context), response));
    }
    catch (ArgumentException ex)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, ex.Message);
    }
}

static async Task<IResult> DeviceHeartbeatAsync(
    DeviceHeartbeatRequest request,
    HttpContext context,
    NetRelayDbContext dbContext,
    KeyManagementService keyManagementService,
    CancellationToken cancellationToken)
{
    if (request == null || request.InstallationId == Guid.Empty || string.IsNullOrWhiteSpace(request.MachineCode))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "请求参数无效。");
    }

    // 1. 验证证书是否合法
    var now = DateTimeOffset.UtcNow;
    if (!request.ReceiptEnvelope.Verify("device-activation", request.ReceiptEnvelope.Nonce, now, keyManagementService.OperationPrivateKey))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, "DEVICE_ACTIVATION_INVALID", "激活回执校验失败。");
    }

    // 2. 解析载荷并校验是否匹配
    var payload = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(request.ReceiptEnvelope.PayloadJson);
    if (payload == null ||
        !payload.TryGetValue("installationId", out var instIdStr) ||
        !payload.TryGetValue("machineCode", out var code) ||
        !Guid.TryParse(instIdStr, out var instId) ||
        instId != request.InstallationId ||
        !string.Equals(code, request.MachineCode, StringComparison.OrdinalIgnoreCase))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, "DEVICE_ACTIVATION_INVALID", "激活回执载荷不匹配。");
    }

    var installation = await dbContext.DeviceInstallations
        .FirstOrDefaultAsync(i => i.InstallationId == request.InstallationId, cancellationToken);

    if (installation == null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "未找到相应的设备安装实例。");
    }

    var shouldUpdateLastSeen = installation.LastSeenAt.AddHours(24) <= now;
    installation.ClientVersion = request.ClientVersion;
    installation.OsVersion = request.OsVersion;

    // 限流：24 小时仅可更新一次 last_seen_at，但客户端版本与系统环境仍要刷新。
    if (installation.LastSeenAt.AddHours(24) > now)
    {
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Ok(new ApiResponse<string>(ApiInfrastructure.GetRequestId(context), "Heartbeat throttled (already updated within 24h)."));
    }

    if (shouldUpdateLastSeen)
    {
        installation.LastSeenAt = now;
    }

    var device = await dbContext.Devices.FindAsync(new object[] { installation.DeviceId }, cancellationToken);
    if (device != null && shouldUpdateLastSeen)
    {
        device.LastSeenAt = now;
    }

    await dbContext.SaveChangesAsync(cancellationToken);
    return Results.Ok(new ApiResponse<string>(ApiInfrastructure.GetRequestId(context), "Heartbeat accepted."));
}

static Task<IResult> GetConnectivityChallengeAsync(
    ConnectivityChallengeRequest request,
    HttpContext context,
    KeyManagementService keyManagementService)
{
    if (request == null || string.IsNullOrWhiteSpace(request.Nonce))
    {
        return Task.FromResult(ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "Nonce 不能为空。"));
    }

    var now = DateTimeOffset.UtcNow;
    var expiresAt = now.AddMinutes(5);

    var payload = new Dictionary<string, object?>
    {
        ["nonce"] = request.Nonce,
        ["timestamp"] = now.ToString("O")
    };

    var envelope = keyManagementService.Sign(
        "connectivity-challenge",
        Guid.NewGuid().ToString("N"),
        now,
        expiresAt,
        payload);

    var response = new ConnectivityChallengeResponse
    {
        Envelope = envelope,
        Certificate = keyManagementService.OperationCertificate
    };

    return Task.FromResult(Results.Ok(new ApiResponse<ConnectivityChallengeResponse>(ApiInfrastructure.GetRequestId(context), response)));
}

static async Task<IResult> GetLatestUpdateAsync(
    string channel,
    string architecture,
    string currentVersion,
    HttpContext context,
    NetRelayDbContext dbContext,
    KeyManagementService keyManagementService,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(architecture) || string.IsNullOrWhiteSpace(currentVersion))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "请求参数不能为空。");
    }

    var latest = await dbContext.Releases
        .Where(r => r.Status == "published" && r.Channel == channel && r.Architecture == architecture)
        .OrderByDescending(r => r.ReleaseDate)
        .FirstOrDefaultAsync(cancellationToken);

    if (latest is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.UpdateNotAvailable, "没有可用的更新。");
    }

    // 比较版本
    if (Version.TryParse(latest.Version, out var latestVer) && Version.TryParse(currentVersion, out var currentVer))
    {
        if (latestVer <= currentVer)
        {
            return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.UpdateNotAvailable, "当前已是最新版本。");
        }
    }
    else if (string.Compare(latest.Version, currentVersion, StringComparison.OrdinalIgnoreCase) <= 0)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.UpdateNotAvailable, "当前已是最新版本。");
    }

    var manifest = new UpdateManifest
    {
        Version = latest.Version,
        Channel = latest.Channel,
        Architecture = latest.Architecture,
        MinUpgradableVersion = latest.MinUpgradableVersion,
        PackageSize = latest.PackageSize,
        Sha256 = latest.Sha256,
        ReleaseDate = latest.ReleaseDate,
        Changelog = latest.Changelog
    };

    var payload = new Dictionary<string, object?>
    {
        ["version"] = manifest.Version,
        ["channel"] = manifest.Channel,
        ["architecture"] = manifest.Architecture,
        ["minUpgradableVersion"] = manifest.MinUpgradableVersion,
        ["packageSize"] = manifest.PackageSize,
        ["sha256"] = manifest.Sha256,
        ["releaseDate"] = manifest.ReleaseDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        ["changelog"] = manifest.Changelog
    };

    var envelope = keyManagementService.Sign(
        "update-manifest",
        Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(5),
        payload);

    var response = new UpdateCheckResponse
    {
        Envelope = envelope,
        Certificate = keyManagementService.OperationCertificate
    };

    return Results.Ok(new ApiResponse<UpdateCheckResponse>(ApiInfrastructure.GetRequestId(context), response));
}

static async Task<IResult> DownloadUpdatePackageAsync(
    string version,
    string filename,
    HttpContext context,
    NetRelayDbContext dbContext,
    ManagedFileStorage storage,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(filename))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "无效的下载请求。");
    }

    var release = await dbContext.Releases
        .Where(r => r.Version == version && r.Status == "published")
        .FirstOrDefaultAsync(cancellationToken);

    if (release is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "版本包不存在。");
    }

    var expectedFilename = Path.GetFileName(release.AssetPath);
    if (!string.Equals(expectedFilename, filename, StringComparison.OrdinalIgnoreCase))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "文件名不匹配。");
    }

    var path = storage.Resolve(StorageArea.Releases, release.AssetPath);
    if (!File.Exists(path))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "物理文件丢失。");
    }

    return Results.File(path, "application/zip", filename);
}

static async Task<IResult> CreateReleaseAsync(
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    AuditService auditService,
    ManagedFileStorage storage,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminCsrfInvalid, "CSRF 校验失败。");
    }

    if (!context.Request.HasFormContentType)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "请求必须是表单形式。");
    }

    var form = await context.Request.ReadFormAsync(cancellationToken);
    var version = form["version"].ToString();
    var channel = form["channel"].ToString();
    var architecture = form["architecture"].ToString();
    var minUpgradableVersion = form["minUpgradableVersion"].ToString();
    var changelog = form["changelog"].ToString();

    if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(channel) ||
        string.IsNullOrWhiteSpace(architecture) || string.IsNullOrWhiteSpace(minUpgradableVersion))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "必填参数缺失。");
    }

    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "更新包文件缺失。");
    }

    var exists = await dbContext.Releases.AnyAsync(r => r.Version == version && r.Channel == channel && r.Architecture == architecture, cancellationToken);
    if (exists)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "该版本更新已存在。");
    }

    var tempName = Guid.NewGuid().ToString("N") + ".zip";
    var tempPath = storage.Resolve(StorageArea.Staging, tempName);

    string sha256Hex;
    long packageSize;
    using (var sha256 = SHA256.Create())
    {
        using (var destStream = File.Create(tempPath))
        {
            using (var srcStream = file.OpenReadStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await srcStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await destStream.WriteAsync(buffer, 0, read, cancellationToken);
                    sha256.TransformBlock(buffer, 0, read, null, 0);
                }
                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            }
            packageSize = destStream.Length;
        }
        sha256Hex = Convert.ToHexString(sha256.Hash!).ToLower();
    }

    var targetPath = $"{channel}/{version}/{architecture}.zip";
    try
    {
        await storage.MoveFromStagingAsync(tempName, StorageArea.Releases, targetPath, cancellationToken);
    }
    catch (Exception ex)
    {
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
        return ApiInfrastructure.Error(context, StatusCodes.Status500InternalServerError, ErrorCodes.ServiceTemporarilyUnavailable, $"保存更新文件失败: {ex.Message}");
    }

    var release = new Release
    {
        Id = Guid.NewGuid(),
        Version = version,
        Channel = channel,
        Architecture = architecture,
        MinUpgradableVersion = minUpgradableVersion,
        PackageSize = packageSize,
        Sha256 = sha256Hex,
        ReleaseDate = DateTimeOffset.UtcNow,
        Changelog = changelog,
        AssetPath = targetPath,
        Status = "draft",
        CreatedAt = DateTimeOffset.UtcNow
    };

    dbContext.Releases.Add(release);
    await dbContext.SaveChangesAsync(cancellationToken);

    await auditService.WriteAsync(
        "release.create",
        "success",
        ApiInfrastructure.GetRequestId(context),
        release.Id,
        details: new { version, channel, architecture },
        cancellationToken: cancellationToken);

    return Results.Ok(new ApiResponse<Release>(ApiInfrastructure.GetRequestId(context), release));
}

static async Task<IResult> GetReleasesAsync(
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    var list = await dbContext.Releases.OrderByDescending(r => r.CreatedAt).ToListAsync(cancellationToken);
    return Results.Ok(new ApiResponse<List<Release>>(ApiInfrastructure.GetRequestId(context), list));
}

static async Task<IResult> PublishReleaseAsync(
    Guid id,
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    AuditService auditService,
    IOptions<ServerOptions> options,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminCsrfInvalid, "CSRF 校验失败。");
    }

    if (session.ReauthenticatedUntil < DateTimeOffset.UtcNow)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminReauthenticationRequired, "敏感操作需要重新进行密码认证。");
    }

    var release = await dbContext.Releases.FindAsync(new object[] { id }, cancellationToken);
    if (release is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "版本记录不存在。");
    }

    if (release.Status != "draft")
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "只有草稿状态的版本可以发布。");
    }

    release.Status = "published";
    release.PublishedAt = DateTimeOffset.UtcNow;
    release.ReleaseDate = DateTimeOffset.UtcNow;

    await dbContext.SaveChangesAsync(cancellationToken);

    await auditService.WriteAsync(
        "release.publish",
        "success",
        ApiInfrastructure.GetRequestId(context),
        release.Id,
        details: new { version = release.Version, channel = release.Channel, architecture = release.Architecture },
        cancellationToken: cancellationToken);

    return Results.Ok(new ApiResponse<object?>(ApiInfrastructure.GetRequestId(context), null));
}

static async Task<IResult> RevokeReleaseAsync(
    Guid id,
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    AuditService auditService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminCsrfInvalid, "CSRF 校验失败。");
    }

    if (session.ReauthenticatedUntil < DateTimeOffset.UtcNow)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminReauthenticationRequired, "敏感操作需要重新进行密码认证。");
    }

    var release = await dbContext.Releases.FindAsync(new object[] { id }, cancellationToken);
    if (release is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "版本记录不存在。");
    }

    release.Status = "revoked";
    release.RevokedAt = DateTimeOffset.UtcNow;

    await dbContext.SaveChangesAsync(cancellationToken);

    await auditService.WriteAsync(
        "release.revoke",
        "success",
        ApiInfrastructure.GetRequestId(context),
        release.Id,
        details: new { version = release.Version, channel = release.Channel, architecture = release.Architecture },
        cancellationToken: cancellationToken);

    return Results.Ok(new ApiResponse<object?>(ApiInfrastructure.GetRequestId(context), null));
}

static async Task<IResult> SubmitFeedbackAsync(
    HttpContext context,
    NetRelayDbContext dbContext,
    ManagedFileStorage storage,
    AuditService auditService,
    CancellationToken cancellationToken)
{
    if (!context.Request.HasFormContentType)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "只接受 Form 内容提交。");
    }

    var form = await context.Request.ReadFormAsync(cancellationToken);
    
    var type = form["type"].ToString();
    var title = form["title"].ToString();
    var content = form["content"].ToString();
    var contact = form["contact"].ToString();
    var deviceId = form["deviceId"].ToString();
    var installationIdStr = form["installationId"].ToString();
    var clientVersion = form["clientVersion"].ToString();
    var osVersion = form["osVersion"].ToString();

    Guid? installationId = null;
    if (Guid.TryParse(installationIdStr, out var parsedGuid))
    {
        installationId = parsedGuid;
    }

    if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "类型、标题和正文不能为空。");
    }

    if (type != "bug" && type != "suggestion" && type != "other")
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "不支持的反馈类型。");
    }

    var feedbackId = Uuid7.Create();
    var hasAttachment = false;
    string? attachmentFilename = null;
    long? attachmentSize = null;

    if (form.Files.GetFile("file") is { } file)
    {
        if (file.Length > 10 * 1024 * 1024)
        {
            return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "附件大小不能超过 10 MB。");
        }

        var ext = Path.GetExtension(file.FileName);
        if (!ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "仅允许上传 .zip 格式的压缩日志包。");
        }

        hasAttachment = true;
        attachmentFilename = file.FileName;
        attachmentSize = file.Length;

        // Save attachment to FeedbackRoot
        var targetPath = storage.Resolve(StorageArea.Feedback, $"feedback_{feedbackId:N}.zip");
        await using var targetStream = File.Create(targetPath);
        await file.CopyToAsync(targetStream, cancellationToken);
    }

    var feedback = new Feedback
    {
        Id = feedbackId,
        Type = type,
        Title = title,
        Content = content,
        Contact = string.IsNullOrWhiteSpace(contact) ? null : contact,
        HasAttachment = hasAttachment,
        AttachmentFilename = attachmentFilename,
        AttachmentSize = attachmentSize,
        Status = "pending",
        CreatedAt = DateTimeOffset.UtcNow,
        DeviceIdHash = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId,
        InstallationId = installationId,
        ClientVersion = string.IsNullOrWhiteSpace(clientVersion) ? null : clientVersion,
        OsVersion = string.IsNullOrWhiteSpace(osVersion) ? null : osVersion
    };

    dbContext.Feedbacks.Add(feedback);
    await dbContext.SaveChangesAsync(cancellationToken);

    await auditService.WriteAsync(
        "feedback.submit",
        "success",
        ApiInfrastructure.GetRequestId(context),
        feedback.Id,
        details: new { type = feedback.Type, title = feedback.Title, hasAttachment = feedback.HasAttachment },
        cancellationToken: cancellationToken);

    return Results.Ok(new ApiResponse<Feedback>(ApiInfrastructure.GetRequestId(context), feedback));
}

static async Task<IResult> GetMyFeedbacksAsync(
    string deviceId,
    HttpContext context,
    NetRelayDbContext dbContext,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(deviceId))
    {
        return ApiInfrastructure.Error(
            context,
            StatusCodes.Status400BadRequest,
            ErrorCodes.RequestInvalid,
            "请求缺少设备指纹参数。");
    }

    var list = await dbContext.Feedbacks
        .Where(f => f.DeviceIdHash == deviceId)
        .OrderByDescending(f => f.CreatedAt)
        .Select(f => new
        {
            f.Id,
            f.Type,
            f.Title,
            f.Content,
            f.Status,
            f.CreatedAt,
            f.StatusUpdatedAt
        })
        .ToListAsync(cancellationToken);

    return Results.Ok(new ApiResponse<object>(ApiInfrastructure.GetRequestId(context), list));
}

static async Task<IResult> GetActiveAnnouncementsAsync(
    string clientVersion,
    HttpContext context,
    NetRelayDbContext dbContext,
    KeyManagementService keyManagementService,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(clientVersion))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "客户端版本号不能为空。");
    }

    var now = DateTimeOffset.UtcNow;
    var allPublished = await dbContext.Announcements
        .Where(a => a.Status == "published" && a.PublishedAt <= now && (a.ExpiresAt == null || a.ExpiresAt > now))
        .ToListAsync(cancellationToken);

    // Filter by target version ranges in memory
    var activeAnnouncements = new List<AnnouncementDto>();
    foreach (var a in allPublished)
    {
        var match = true;
        if (!string.IsNullOrWhiteSpace(a.TargetVersionMin))
        {
            if (Version.TryParse(a.TargetVersionMin, out var minVer) && Version.TryParse(clientVersion, out var cVer))
            {
                if (cVer < minVer) match = false;
            }
            else if (string.Compare(clientVersion, a.TargetVersionMin, StringComparison.OrdinalIgnoreCase) < 0)
            {
                match = false;
            }
        }
        if (match && !string.IsNullOrWhiteSpace(a.TargetVersionMax))
        {
            if (Version.TryParse(a.TargetVersionMax, out var maxVer) && Version.TryParse(clientVersion, out var cVer))
            {
                if (cVer > maxVer) match = false;
            }
            else if (string.Compare(clientVersion, a.TargetVersionMax, StringComparison.OrdinalIgnoreCase) > 0)
            {
                match = false;
            }
        }

        if (match)
        {
            activeAnnouncements.Add(new AnnouncementDto
            {
                Id = a.Id,
                Title = a.Title,
                Content = a.Content,
                Severity = a.Severity,
                TargetVersionMin = a.TargetVersionMin,
                TargetVersionMax = a.TargetVersionMax,
                DisplayTrigger = a.DisplayTrigger,
                PublishedAt = a.PublishedAt ?? a.CreatedAt,
                ExpiresAt = a.ExpiresAt
            });
        }
    }

    var payload = new Dictionary<string, object?>
    {
        ["announcements"] = activeAnnouncements.Select(a => new Dictionary<string, object?>
        {
            ["id"] = a.Id.ToString("D"),
            ["title"] = a.Title,
            ["content"] = a.Content,
            ["severity"] = a.Severity,
            ["targetVersionMin"] = a.TargetVersionMin,
            ["targetVersionMax"] = a.TargetVersionMax,
            ["displayTrigger"] = a.DisplayTrigger,
            ["publishedAt"] = a.PublishedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["expiresAt"] = a.ExpiresAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        }).ToList()
    };

    var envelope = keyManagementService.Sign(
        "announcement",
        Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(5),
        payload);

    var response = new AnnouncementCheckResponse
    {
        Envelope = envelope,
        Certificate = keyManagementService.OperationCertificate
    };

    return Results.Ok(new ApiResponse<AnnouncementCheckResponse>(ApiInfrastructure.GetRequestId(context), response));
}

static async Task<IResult> GetFeedbacksAsync(
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    var list = await dbContext.Feedbacks
        .OrderByDescending(f => f.CreatedAt)
        .ToListAsync(cancellationToken);

    return Results.Ok(new ApiResponse<List<Feedback>>(ApiInfrastructure.GetRequestId(context), list));
}

static async Task<IResult> GetRegisteredDevicesAsync(
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    var list = await dbContext.DeviceInstallations
        .Include(di => di.Device)
        .OrderByDescending(di => di.LastSeenAt)
        .Select(di => new
        {
            DeviceIdHash = di.Device != null ? di.Device.DeviceIdHash : null,
            InstallationId = di.InstallationId,
            ClientVersion = di.ClientVersion,
            OsVersion = di.OsVersion,
            FirstSeenAt = di.FirstSeenAt,
            LastSeenAt = di.LastSeenAt
        })
        .ToListAsync(cancellationToken);

    return Results.Ok(new ApiResponse<object>(ApiInfrastructure.GetRequestId(context), list));
}

static async Task<IResult> UpdateFeedbackStatusAsync(
    Guid id,
    FeedbackStatusUpdateRequest request,
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    AuditService auditService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminCsrfInvalid, "CSRF 校验失败。");
    }

    if (session.ReauthenticatedUntil < DateTimeOffset.UtcNow)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminReauthenticationRequired, "敏感操作需要重新进行密码认证。");
    }

    if (string.IsNullOrWhiteSpace(request.Status) || (request.Status != "pending" && request.Status != "resolved" && request.Status != "ignored"))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "非法状态值。");
    }

    var feedback = await dbContext.Feedbacks.FindAsync(new object[] { id }, cancellationToken);
    if (feedback is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "反馈记录不存在。");
    }

    var oldStatus = feedback.Status;
    feedback.Status = request.Status;
    feedback.StatusUpdatedAt = DateTimeOffset.UtcNow;

    await dbContext.SaveChangesAsync(cancellationToken);

    await auditService.WriteAsync(
        "feedback.update_status",
        "success",
        ApiInfrastructure.GetRequestId(context),
        feedback.Id,
        details: new { oldStatus, newStatus = feedback.Status },
        cancellationToken: cancellationToken);

    return Results.Ok(new ApiResponse<Feedback>(ApiInfrastructure.GetRequestId(context), feedback));
}

static async Task<IResult> DownloadFeedbackAttachmentAsync(
    Guid id,
    HttpContext context,
    NetRelayDbContext dbContext,
    ManagedFileStorage storage,
    AdminAuthService authService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    if (session.ReauthenticatedUntil < DateTimeOffset.UtcNow)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminReauthenticationRequired, "敏感操作需要重新进行密码认证。");
    }

    var feedback = await dbContext.Feedbacks.FindAsync(new object[] { id }, cancellationToken);
    if (feedback is null || !feedback.HasAttachment)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "附件文件不存在。");
    }

    var path = storage.Resolve(StorageArea.Feedback, $"feedback_{feedback.Id:N}.zip");
    if (!File.Exists(path))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "附件物理文件丢失。");
    }

    var stream = File.OpenRead(path);
    return Results.File(stream, "application/zip", feedback.AttachmentFilename ?? $"feedback_{feedback.Id:N}.zip");
}

static async Task<IResult> CreateAnnouncementAsync(
    AnnouncementCreateRequest request,
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminCsrfInvalid, "CSRF 校验失败。");
    }

    if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content) || string.IsNullOrWhiteSpace(request.Severity) || string.IsNullOrWhiteSpace(request.DisplayTrigger))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "标题、内容、等级与展示触发器均不能为空。");
    }

    if (request.Severity != "normal" && request.Severity != "important" && request.Severity != "critical")
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "无效的通知等级。");
    }

    if (request.DisplayTrigger != "once_per_device" && request.DisplayTrigger != "every_startup")
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "无效的展示触发类型。");
    }

    var announcement = new Announcement
    {
        Id = Uuid7.Create(),
        Title = request.Title,
        Content = request.Content,
        Severity = request.Severity,
        TargetVersionMin = string.IsNullOrWhiteSpace(request.TargetVersionMin) ? null : request.TargetVersionMin,
        TargetVersionMax = string.IsNullOrWhiteSpace(request.TargetVersionMax) ? null : request.TargetVersionMax,
        DisplayTrigger = request.DisplayTrigger,
        Status = "draft",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = request.ExpiresAt
    };

    dbContext.Announcements.Add(announcement);
    await dbContext.SaveChangesAsync(cancellationToken);

    return Results.Ok(new ApiResponse<Announcement>(ApiInfrastructure.GetRequestId(context), announcement));
}

static async Task<IResult> EditAnnouncementAsync(
    Guid id,
    AnnouncementCreateRequest request,
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminCsrfInvalid, "CSRF 校验失败。");
    }

    if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content) || string.IsNullOrWhiteSpace(request.Severity) || string.IsNullOrWhiteSpace(request.DisplayTrigger))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "标题、内容、等级与展示触发器均不能为空。");
    }

    var announcement = await dbContext.Announcements.FindAsync(new object[] { id }, cancellationToken);
    if (announcement is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "公告不存在。");
    }

    if (announcement.Status != "draft")
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "非草稿状态公告不可编辑。");
    }

    announcement.Title = request.Title;
    announcement.Content = request.Content;
    announcement.Severity = request.Severity;
    announcement.TargetVersionMin = string.IsNullOrWhiteSpace(request.TargetVersionMin) ? null : request.TargetVersionMin;
    announcement.TargetVersionMax = string.IsNullOrWhiteSpace(request.TargetVersionMax) ? null : request.TargetVersionMax;
    announcement.DisplayTrigger = request.DisplayTrigger;
    announcement.ExpiresAt = request.ExpiresAt;

    await dbContext.SaveChangesAsync(cancellationToken);

    return Results.Ok(new ApiResponse<Announcement>(ApiInfrastructure.GetRequestId(context), announcement));
}

static async Task<IResult> PublishAnnouncementAsync(
    Guid id,
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    AuditService auditService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminCsrfInvalid, "CSRF 校验失败。");
    }

    if (session.ReauthenticatedUntil < DateTimeOffset.UtcNow)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminReauthenticationRequired, "敏感操作需要重新进行密码认证。");
    }

    var announcement = await dbContext.Announcements.FindAsync(new object[] { id }, cancellationToken);
    if (announcement is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "公告不存在。");
    }

    if (announcement.Status != "draft")
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "只有草稿状态公告才可发布。");
    }

    announcement.Status = "published";
    announcement.PublishedAt = DateTimeOffset.UtcNow;

    await dbContext.SaveChangesAsync(cancellationToken);

    await auditService.WriteAsync(
        "announcement.publish",
        "success",
        ApiInfrastructure.GetRequestId(context),
        announcement.Id,
        details: new { title = announcement.Title },
        cancellationToken: cancellationToken);

    return Results.Ok(new ApiResponse<Announcement>(ApiInfrastructure.GetRequestId(context), announcement));
}

static async Task<IResult> RevokeAnnouncementAsync(
    Guid id,
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    AuditService auditService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminCsrfInvalid, "CSRF 校验失败。");
    }

    if (session.ReauthenticatedUntil < DateTimeOffset.UtcNow)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminReauthenticationRequired, "敏感操作需要重新进行密码认证。");
    }

    var announcement = await dbContext.Announcements.FindAsync(new object[] { id }, cancellationToken);
    if (announcement is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "公告不存在。");
    }

    if (announcement.Status != "published")
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "只有已发布公告才可撤回。");
    }

    announcement.Status = "revoked";
    announcement.RevokedAt = DateTimeOffset.UtcNow;

    await dbContext.SaveChangesAsync(cancellationToken);

    await auditService.WriteAsync(
        "announcement.revoke",
        "success",
        ApiInfrastructure.GetRequestId(context),
        announcement.Id,
        details: new { title = announcement.Title },
        cancellationToken: cancellationToken);

    return Results.Ok(new ApiResponse<Announcement>(ApiInfrastructure.GetRequestId(context), announcement));
}

static async Task<IResult> GetAnnouncementsAsync(
    HttpContext context,
    NetRelayDbContext dbContext,
    CancellationToken cancellationToken)
{
    var list = await dbContext.Announcements
        .OrderByDescending(a => a.CreatedAt)
        .ToListAsync(cancellationToken);

    return Results.Ok(new ApiResponse<List<Announcement>>(ApiInfrastructure.GetRequestId(context), list));
}

static async Task<IResult> GetDeviceBlocksAsync(
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    var list = await dbContext.DeviceBlocks
        .OrderByDescending(b => b.CreatedAt)
        .Select(b => new DeviceBlockDto
        {
            Id = b.Id,
            DeviceId = b.DeviceId,
            InstallationId = b.InstallationId,
            Reason = b.Reason,
            Status = b.Status,
            CreatedAt = b.CreatedAt,
            ExpiresAt = b.ExpiresAt,
            RevokedAt = b.RevokedAt
        })
        .ToListAsync(cancellationToken);

    return Results.Ok(new ApiResponse<List<DeviceBlockDto>>(ApiInfrastructure.GetRequestId(context), list));
}

static async Task<IResult> CreateDeviceBlockAsync(
    DeviceBlockCreateRequest request,
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    AuditService auditService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminCsrfInvalid, "CSRF 校验失败。");
    }

    if (session.ReauthenticatedUntil < DateTimeOffset.UtcNow)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminReauthenticationRequired, "敏感操作需要重新进行密码认证。");
    }

    if (string.IsNullOrWhiteSpace(request.Reason))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "封锁原因不能为空。");
    }

    if (request.DeviceId == null && request.InstallationId == null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "必须指定 DeviceId 或 InstallationId 进行封锁。");
    }

    if (request.DeviceId.HasValue)
    {
        var deviceExists = await dbContext.Devices.AnyAsync(d => d.Id == request.DeviceId.Value, cancellationToken);
        if (!deviceExists)
        {
            return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "指定的设备不存在。");
        }
    }
    if (request.InstallationId.HasValue)
    {
        var instExists = await dbContext.DeviceInstallations.AnyAsync(i => i.Id == request.InstallationId.Value, cancellationToken);
        if (!instExists)
        {
            return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "指定的安装实例不存在。");
        }
    }

    var block = new DeviceBlock
    {
        Id = Uuid7.Create(),
        DeviceId = request.DeviceId,
        InstallationId = request.InstallationId,
        Reason = request.Reason,
        Status = "active",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = request.ExpiresAt
    };

    dbContext.DeviceBlocks.Add(block);
    await dbContext.SaveChangesAsync(cancellationToken);

    await auditService.WriteAsync(
        "device_block.create",
        "success",
        ApiInfrastructure.GetRequestId(context),
        block.Id,
        "device_block",
        block.Id.ToString(),
        details: new { deviceId = block.DeviceId, installationId = block.InstallationId, reason = block.Reason },
        cancellationToken: cancellationToken);

    var dto = new DeviceBlockDto
    {
        Id = block.Id,
        DeviceId = block.DeviceId,
        InstallationId = block.InstallationId,
        Reason = block.Reason,
        Status = block.Status,
        CreatedAt = block.CreatedAt,
        ExpiresAt = block.ExpiresAt,
        RevokedAt = block.RevokedAt
    };

    return Results.Ok(new ApiResponse<DeviceBlockDto>(ApiInfrastructure.GetRequestId(context), dto));
}

static async Task<IResult> RevokeDeviceBlockAsync(
    Guid id,
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    AuditService auditService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminCsrfInvalid, "CSRF 校验失败。");
    }

    if (session.ReauthenticatedUntil < DateTimeOffset.UtcNow)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminReauthenticationRequired, "敏感操作需要重新进行密码认证。");
    }

    var block = await dbContext.DeviceBlocks.FindAsync(new object[] { id }, cancellationToken);
    if (block is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "封锁记录不存在。");
    }

    if (block.Status == "revoked")
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "封锁记录已是撤销状态。");
    }

    block.Status = "revoked";
    block.RevokedAt = DateTimeOffset.UtcNow;

    await dbContext.SaveChangesAsync(cancellationToken);

    await auditService.WriteAsync(
        "device_block.revoke",
        "success",
        ApiInfrastructure.GetRequestId(context),
        block.Id,
        "device_block",
        block.Id.ToString(),
        details: new { reason = block.Reason },
        cancellationToken: cancellationToken);

    return Results.Ok(new ApiResponse<object?>(ApiInfrastructure.GetRequestId(context), null));
}

static async Task<IResult> GetGlobalPoliciesAsync(
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    var list = await dbContext.GlobalPolicies
        .OrderByDescending(p => p.CreatedAt)
        .Select(p => new GlobalPolicyDto
        {
            Id = p.Id,
            Type = p.Type,
            TargetVersionMin = p.TargetVersionMin,
            TargetVersionMax = p.TargetVersionMax,
            Reason = p.Reason,
            AllowUpdate = p.AllowUpdate,
            Status = p.Status,
            CreatedAt = p.CreatedAt,
            ExpiresAt = p.ExpiresAt,
            RevokedAt = p.RevokedAt
        })
        .ToListAsync(cancellationToken);

    return Results.Ok(new ApiResponse<List<GlobalPolicyDto>>(ApiInfrastructure.GetRequestId(context), list));
}

static async Task<IResult> CreateGlobalPolicyAsync(
    GlobalPolicyCreateRequest request,
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    AuditService auditService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminCsrfInvalid, "CSRF 校验失败。");
    }

    if (session.ReauthenticatedUntil < DateTimeOffset.UtcNow)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminReauthenticationRequired, "敏感操作需要重新进行密码认证。");
    }

    if (string.IsNullOrWhiteSpace(request.Reason))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "策略原因不能为空。");
    }

    if (request.Type != "global" && request.Type != "version_range")
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "无效的策略类型。");
    }

    if (request.Type == "version_range" && string.IsNullOrWhiteSpace(request.TargetVersionMin) && string.IsNullOrWhiteSpace(request.TargetVersionMax))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "版本范围策略必须指定至少一个版本限制。");
    }

    var policy = new GlobalPolicy
    {
        Id = Uuid7.Create(),
        Type = request.Type,
        TargetVersionMin = string.IsNullOrWhiteSpace(request.TargetVersionMin) ? null : request.TargetVersionMin.Trim(),
        TargetVersionMax = string.IsNullOrWhiteSpace(request.TargetVersionMax) ? null : request.TargetVersionMax.Trim(),
        Reason = request.Reason,
        AllowUpdate = request.AllowUpdate,
        Status = "active",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = request.ExpiresAt
    };

    dbContext.GlobalPolicies.Add(policy);
    await dbContext.SaveChangesAsync(cancellationToken);

    await auditService.WriteAsync(
        "global_policy.create",
        "success",
        ApiInfrastructure.GetRequestId(context),
        policy.Id,
        "global_policy",
        policy.Id.ToString(),
        details: new { type = policy.Type, versionMin = policy.TargetVersionMin, versionMax = policy.TargetVersionMax, reason = policy.Reason, allowUpdate = policy.AllowUpdate },
        cancellationToken: cancellationToken);

    var dto = new GlobalPolicyDto
    {
        Id = policy.Id,
        Type = policy.Type,
        TargetVersionMin = policy.TargetVersionMin,
        TargetVersionMax = policy.TargetVersionMax,
        Reason = policy.Reason,
        AllowUpdate = policy.AllowUpdate,
        Status = policy.Status,
        CreatedAt = policy.CreatedAt,
        ExpiresAt = policy.ExpiresAt,
        RevokedAt = policy.RevokedAt
    };

    return Results.Ok(new ApiResponse<GlobalPolicyDto>(ApiInfrastructure.GetRequestId(context), dto));
}

static async Task<IResult> RevokeGlobalPolicyAsync(
    Guid id,
    HttpContext context,
    NetRelayDbContext dbContext,
    AdminAuthService authService,
    AuditService auditService,
    CancellationToken cancellationToken)
{
    var session = await ResolveRequiredSessionAsync(context, authService, cancellationToken);
    if (session is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status401Unauthorized, ErrorCodes.AdminAuthenticationRequired, "需要管理员认证。");
    }

    if (!authService.VerifyCsrf(session, context.Request.Headers[Protocol.CsrfHeader]))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminCsrfInvalid, "CSRF 校验失败。");
    }

    if (session.ReauthenticatedUntil < DateTimeOffset.UtcNow)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status403Forbidden, ErrorCodes.AdminReauthenticationRequired, "敏感操作需要重新进行密码认证。");
    }

    var policy = await dbContext.GlobalPolicies.FindAsync(new object[] { id }, cancellationToken);
    if (policy is null)
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, "全局策略不存在。");
    }

    if (policy.Status == "revoked")
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "策略已是撤销状态。");
    }

    policy.Status = "revoked";
    policy.RevokedAt = DateTimeOffset.UtcNow;

    await dbContext.SaveChangesAsync(cancellationToken);

    await auditService.WriteAsync(
        "global_policy.revoke",
        "success",
        ApiInfrastructure.GetRequestId(context),
        policy.Id,
        "global_policy",
        policy.Id.ToString(),
        details: new { reason = policy.Reason },
        cancellationToken: cancellationToken);

    return Results.Ok(new ApiResponse<object?>(ApiInfrastructure.GetRequestId(context), null));
}

static async Task<IResult> EvaluatePolicyAsync(
    PolicyEvaluateRequest request,
    HttpContext context,
    NetRelayDbContext dbContext,
    KeyManagementService keyManagementService,
    IOptions<ServerOptions> options,
    CancellationToken cancellationToken)
{
    if (request == null || string.IsNullOrWhiteSpace(request.DeviceId) || request.InstallationId == Guid.Empty || string.IsNullOrWhiteSpace(request.ClientVersion))
    {
        return ApiInfrastructure.Error(context, StatusCodes.Status400BadRequest, ErrorCodes.RequestInvalid, "评估请求参数不全。");
    }

    var now = DateTimeOffset.UtcNow;

    var device = await dbContext.Devices.FirstOrDefaultAsync(d => d.DeviceIdHash == request.DeviceId, cancellationToken);
    var installation = await dbContext.DeviceInstallations.FirstOrDefaultAsync(i => i.InstallationId == request.InstallationId, cancellationToken);

    DeviceBlock? matchingBlock = null;
    if (device != null || installation != null)
    {
        matchingBlock = await dbContext.DeviceBlocks
            .Where(b => b.Status == "active" && (b.ExpiresAt == null || b.ExpiresAt > now))
            .Where(b => (device != null && b.DeviceId == device.Id) || (installation != null && b.InstallationId == installation.Id))
            .FirstOrDefaultAsync(cancellationToken);
    }

    var globalPolicies = await dbContext.GlobalPolicies
        .Where(p => p.Status == "active" && (p.ExpiresAt == null || p.ExpiresAt > now))
        .ToListAsync(cancellationToken);

    GlobalPolicy? matchingGlobalPolicy = null;
    foreach (var policy in globalPolicies)
    {
        if (policy.Type == "global")
        {
            matchingGlobalPolicy = policy;
            break;
        }
        else if (policy.Type == "version_range")
        {
            var inRange = true;
            var clientVerStr = request.ClientVersion;
            if (Version.TryParse(clientVerStr, out var cVer))
            {
                if (!string.IsNullOrWhiteSpace(policy.TargetVersionMin) && Version.TryParse(policy.TargetVersionMin, out var minVer) && cVer < minVer)
                {
                    inRange = false;
                }
                if (!string.IsNullOrWhiteSpace(policy.TargetVersionMax) && Version.TryParse(policy.TargetVersionMax, out var maxVer) && cVer > maxVer)
                {
                    inRange = false;
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(policy.TargetVersionMin) && string.Compare(clientVerStr, policy.TargetVersionMin, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    inRange = false;
                }
                if (!string.IsNullOrWhiteSpace(policy.TargetVersionMax) && string.Compare(clientVerStr, policy.TargetVersionMax, StringComparison.OrdinalIgnoreCase) > 0)
                {
                    inRange = false;
                }
            }

            if (inRange)
            {
                matchingGlobalPolicy = policy;
                break;
            }
        }
    }

    bool isBlocked = false;
    string? reason = null;
    DateTimeOffset? expiresAt = null;
    bool allowUpdate = true;

    if (matchingBlock != null || matchingGlobalPolicy != null)
    {
        isBlocked = true;

        if (matchingBlock != null && matchingGlobalPolicy != null)
        {
            if (matchingBlock.ExpiresAt == null || matchingGlobalPolicy.ExpiresAt == null)
            {
                expiresAt = null;
                if (matchingGlobalPolicy.ExpiresAt == null)
                {
                    reason = matchingGlobalPolicy.Reason;
                    allowUpdate = matchingGlobalPolicy.AllowUpdate;
                }
                else
                {
                    reason = matchingBlock.Reason;
                    allowUpdate = true;
                }
            }
            else if (matchingBlock.ExpiresAt.Value > matchingGlobalPolicy.ExpiresAt.Value)
            {
                expiresAt = matchingBlock.ExpiresAt;
                reason = matchingBlock.Reason;
                allowUpdate = true;
            }
            else
            {
                expiresAt = matchingGlobalPolicy.ExpiresAt;
                reason = matchingGlobalPolicy.Reason;
                allowUpdate = matchingGlobalPolicy.AllowUpdate;
            }
        }
        else if (matchingGlobalPolicy != null)
        {
            expiresAt = matchingGlobalPolicy.ExpiresAt;
            reason = matchingGlobalPolicy.Reason;
            allowUpdate = matchingGlobalPolicy.AllowUpdate;
        }
        else
        {
            expiresAt = matchingBlock!.ExpiresAt;
            reason = matchingBlock.Reason;
            allowUpdate = true;
        }
    }

    var appealUrl = $"{options.Value.PublicBaseUrl.TrimEnd('/')}/appeal";

    var payload = new Dictionary<string, object?>
    {
        ["isBlocked"] = isBlocked,
        ["reason"] = reason,
        ["appealUrl"] = isBlocked ? appealUrl : null,
        ["expiresAt"] = expiresAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        ["allowUpdate"] = allowUpdate
    };

    var envelope = keyManagementService.Sign(
        "policy",
        Guid.NewGuid().ToString("N"),
        now,
        now.AddMinutes(5),
        payload);

    var response = new PolicyEvaluateResponse
    {
        Envelope = envelope,
        Certificate = keyManagementService.OperationCertificate
    };

    return Results.Ok(new ApiResponse<PolicyEvaluateResponse>(ApiInfrastructure.GetRequestId(context), response));
}

public partial class Program;
