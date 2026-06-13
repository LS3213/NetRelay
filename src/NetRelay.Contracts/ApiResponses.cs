namespace NetRelay.Contracts;

public sealed record ApiResponse<T>(string RequestId, T Data);

public sealed record ApiError(
    string Code,
    string Message,
    bool Retryable,
    int? RetryAfterSeconds = null,
    object? Details = null);

public sealed record ApiErrorResponse(string RequestId, ApiError Error);
