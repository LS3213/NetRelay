using NetRelay.Contracts;

namespace NetRelay.Server.Infrastructure;

public static class ApiInfrastructure
{
    public const string RequestIdItemKey = "NetRelay.RequestId";

    public static string GetRequestId(HttpContext context) =>
        context.Items.TryGetValue(RequestIdItemKey, out var value) && value is string requestId
            ? requestId
            : context.TraceIdentifier;

    public static IResult Error(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        bool retryable = false,
        int? retryAfterSeconds = null,
        object? details = null) =>
        Results.Json(
            new ApiErrorResponse(
                GetRequestId(context),
                new ApiError(code, message, retryable, retryAfterSeconds, details)),
            statusCode: statusCode);
}

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled API exception for request {RequestId}.", context.TraceIdentifier);
            if (!context.Response.HasStarted)
            {
                await ApiInfrastructure.Error(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    ErrorCodes.ServiceTemporarilyUnavailable,
                    "服务暂时不可用。",
                    retryable: true).ExecuteAsync(context);
            }
        }
    }
}

public sealed class RequestIdentityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[Protocol.RequestIdHeader].FirstOrDefault();
        var requestId = IsValid(incoming) ? incoming! : Uuid7.Create().ToString();
        context.TraceIdentifier = requestId;
        context.Items[ApiInfrastructure.RequestIdItemKey] = requestId;
        context.Response.Headers[Protocol.RequestIdHeader] = requestId;

        var isLegacyUpdatePackageRequest =
            (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
            context.Request.Path.StartsWithSegments("/api/v1/updates") &&
            context.Request.Path.Value?.Contains("/download/", StringComparison.OrdinalIgnoreCase) == true;

        if (context.Request.Path.StartsWithSegments("/api/v1") &&
            !isLegacyUpdatePackageRequest &&
            (!int.TryParse(context.Request.Headers[Protocol.VersionHeader], out var protocolVersion) ||
             protocolVersion != Protocol.CurrentVersion))
        {
            await ApiInfrastructure.Error(
                context,
                StatusCodes.Status400BadRequest,
                ErrorCodes.ProtocolUnsupported,
                "不支持的协议版本。").ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static bool IsValid(string? requestId) =>
        !string.IsNullOrWhiteSpace(requestId) &&
        requestId.Length <= 100 &&
        requestId.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
