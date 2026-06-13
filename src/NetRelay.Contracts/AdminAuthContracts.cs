namespace NetRelay.Contracts;

public sealed record AdminLoginRequest(string Username, string Password);

public sealed record AdminLoginChallenge(string ChallengeToken, DateTimeOffset ExpiresAt);

public sealed record AdminTotpRequest(string ChallengeToken, string Code);

public sealed record AdminSessionResponse(
    string Username,
    string CsrfToken,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ReauthenticationExpiresAt);

public sealed record AdminReauthenticateRequest(string Password);

public sealed record AdminIdentityResponse(
    string Username,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ReauthenticationExpiresAt);

public sealed record AdminCsrfResponse(string CsrfToken);
