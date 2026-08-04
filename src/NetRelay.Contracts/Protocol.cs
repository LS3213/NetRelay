namespace NetRelay.Contracts;

public static class Protocol
{
    public const int CurrentVersion = 1;
    public const string ProductVersion = "1.2.14";
    public const string VersionHeader = "X-NetRelay-Protocol";
    public const string ClientVersionHeader = "X-NetRelay-Version";
    public const string RequestIdHeader = "X-Request-Id";
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string CsrfHeader = "X-NetRelay-Csrf";
}

public static class ErrorCodes
{
    public const string RequestInvalid = "REQUEST_INVALID";
    public const string ProtocolUnsupported = "PROTOCOL_UNSUPPORTED";
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";
    public const string RateLimited = "RATE_LIMITED";
    public const string ServiceTemporarilyUnavailable = "SERVICE_TEMPORARILY_UNAVAILABLE";
    public const string AdminAuthenticationRequired = "ADMIN_AUTHENTICATION_REQUIRED";
    public const string AdminTotpRequired = "ADMIN_TOTP_REQUIRED";
    public const string AdminReauthenticationRequired = "ADMIN_REAUTHENTICATION_REQUIRED";
    public const string AdminCsrfInvalid = "ADMIN_CSRF_INVALID";
    public const string AdminAccountLocked = "ADMIN_ACCOUNT_LOCKED";
    public const string UpdateNotAvailable = "UPDATE_NOT_AVAILABLE";
}
