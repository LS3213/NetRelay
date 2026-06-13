using System.Security.Cryptography;
using System.Text;
using QRCoder;
using NetRelay.Server.Security;

namespace NetRelay.Server.Installation;

public static class InstallationEndpoints
{
    public const string InstallTokenHeader = "X-NetRelay-Install-Token";

    public static void MapInstallationEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => Results.Redirect("/install/"));
        app.MapGet("/health/live", () => Results.Ok(new { status = "install-mode" }));
        app.MapGet(
            "/install/",
            (IWebHostEnvironment environment) =>
                InstallAsset("index.html", "text/html; charset=utf-8", environment))
            .AddEndpointFilter(DisableCache);
        app.MapGet(
            "/install/install.css",
            (IWebHostEnvironment environment) =>
                InstallAsset("install.css", "text/css; charset=utf-8", environment))
            .AddEndpointFilter(DisableCache);
        app.MapGet(
            "/install/install.js",
            (IWebHostEnvironment environment) =>
                InstallAsset("install.js", "text/javascript; charset=utf-8", environment))
            .AddEndpointFilter(DisableCache);
        app.MapGet(
            "/install/api/status",
            (InstallationState state) => Results.Ok(
                new InstallationStatusResponse(state.IsInstalled, state.IsInstallMode)));
        app.MapPost(
            "/install/api/test-database",
            TestDatabaseAsync);
        app.MapPost(
            "/install/api/totp-setup",
            CreateTotpSetup);
        app.MapPost(
            "/install/api/totp-verify",
            VerifyTotpSetup);
        app.MapPost(
            "/install/api/complete",
            CompleteInstallationAsync);
        app.MapFallback(() => Results.Redirect("/install/"));
    }

    private static async Task<IResult> TestDatabaseAsync(
        DatabaseInstallationRequest request,
        HttpContext context,
        InstallationState state,
        InstallationService installer,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(context, state))
        {
            return Results.Json(new { error = "安装令牌无效。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            await installer.TestDatabaseAsync(request, cancellationToken);
            return Results.Ok(new { connected = true });
        }
        catch (Exception exception) when (
            exception is ArgumentException or MySqlConnector.MySqlException or InvalidOperationException)
        {
            return Results.Json(
                new { error = "数据库连接失败，请检查地址、数据库、账号、密码与权限。" },
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> CompleteInstallationAsync(
        InstallationRequest request,
        HttpContext context,
        InstallationState state,
        InstallationService installer,
        IHostApplicationLifetime lifetime,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(context, state))
        {
            return Results.Json(new { error = "安装令牌无效。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            await installer.InstallAsync(request, cancellationToken);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                lifetime.StopApplication();
            });
            return Results.Ok(new InstallationResultResponse(true, true));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            MySqlConnector.MySqlException or
            Microsoft.EntityFrameworkCore.DbUpdateException or
            IOException or
            UnauthorizedAccessException)
        {
            return Results.Json(
                new { error = exception.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult CreateTotpSetup(
        TotpSetupRequest request,
        HttpContext context,
        InstallationState state)
    {
        if (!IsAuthorized(context, state))
        {
            return Results.Json(new { error = "安装令牌无效。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        var accountName = string.IsNullOrWhiteSpace(request.AccountName)
            ? "admin"
            : request.AccountName.Trim();
        if (accountName.Length > 100)
        {
            return Results.Json(
                new { error = "管理员用户名不能超过 100 个字符。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var secret = EncodeBase32(RandomNumberGenerator.GetBytes(20));
        const string issuer = "NetRelay";
        var label = Uri.EscapeDataString($"{issuer}:{accountName}");
        var uri = $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
        using var qrData = QRCodeGenerator.GenerateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var svg = new SvgQRCode(qrData).GetGraphic(5);
        var dataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        return Results.Ok(new TotpSetupResponse(secret, dataUri));
    }

    private static IResult VerifyTotpSetup(
        TotpVerificationRequest request,
        HttpContext context,
        InstallationState state)
    {
        if (!IsAuthorized(context, state))
        {
            return Results.Json(new { error = "安装令牌无效。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!TotpService.IsValidSecret(request.Secret) ||
            !TotpService.Verify(request.Secret, request.Code, DateTimeOffset.UtcNow))
        {
            return Results.Json(
                new { error = "动态验证码无效，请确认验证器和服务器时间已经同步。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Ok(new { verified = true });
    }

    private static bool IsAuthorized(HttpContext context, InstallationState state) =>
        state.IsInstallMode &&
        state.VerifyInstallToken(context.Request.Headers[InstallTokenHeader].FirstOrDefault());

    private static async ValueTask<object?> DisableCache(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.HttpContext.Response.Headers.Pragma = "no-cache";
        context.HttpContext.Response.Headers.Expires = "0";
        return await next(context);
    }

    private static IResult InstallAsset(
        string fileName,
        string contentType,
        IWebHostEnvironment environment)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "InstallAssets", fileName);
        var path = File.Exists(outputPath)
            ? outputPath
            : Path.Combine(environment.ContentRootPath, "InstallAssets", fileName);
        return Results.File(path, contentType);
    }

    private static string EncodeBase32(ReadOnlySpan<byte> bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                output.Append(alphabet[(buffer >> bitsLeft) & 31]);
                buffer &= (1 << bitsLeft) - 1;
            }
        }

        if (bitsLeft > 0)
        {
            output.Append(alphabet[(buffer << (5 - bitsLeft)) & 31]);
        }

        return output.ToString();
    }
}
