namespace NetRelay.Server.Installation;

public sealed record DatabaseInstallationRequest(
    string Host,
    int Port,
    string Database,
    string Username,
    string Password);

public sealed record InstallationRequest(
    DatabaseInstallationRequest Database,
    string PublicBaseUrl,
    string GithubRepository,
    string AdminUsername,
    string AdminPassword,
    string TotpSecret,
    string TotpCode,
    string DataRoot);

public sealed record InstallationStatusResponse(bool Installed, bool InstallMode);

public sealed record InstallationResultResponse(bool Installed, bool RestartRequired);

public sealed record TotpSetupRequest(string AccountName);

public sealed record TotpSetupResponse(string Secret, string QrCodeDataUri);

public sealed record TotpVerificationRequest(string Secret, string Code);
