using System.Threading.RateLimiting;
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

public partial class Program;
