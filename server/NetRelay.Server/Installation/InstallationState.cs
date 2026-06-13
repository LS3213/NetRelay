using System.Security.Cryptography;

namespace NetRelay.Server.Installation;

public sealed class InstallationState
{
    public const string ConfigDirectoryEnvironment = "NETRELAY_CONFIG_DIR";
    public const string InstallModeEnvironment = "NETRELAY_INSTALL_MODE";
    public const string InstallTokenEnvironment = "NETRELAY_INSTALL_TOKEN";

    public InstallationState()
    {
        ConfigDirectory = Path.GetFullPath(
            Environment.GetEnvironmentVariable(ConfigDirectoryEnvironment) ??
            Path.Combine(AppContext.BaseDirectory, "config"));
        RuntimeConfigPath = Path.Combine(ConfigDirectory, "runtime-config.json");
        InstallLockPath = Path.Combine(ConfigDirectory, "installed.lock");
        InstallTokenPath = Path.Combine(ConfigDirectory, "install.token");
    }

    public string ConfigDirectory { get; }
    public string RuntimeConfigPath { get; }
    public string InstallLockPath { get; }
    public string InstallTokenPath { get; }
    public bool HasInstallLock => File.Exists(InstallLockPath);
    public bool IsInstalled => File.Exists(InstallLockPath) && File.Exists(RuntimeConfigPath);
    public bool IsInstallMode =>
        !HasInstallLock &&
        string.Equals(
            Environment.GetEnvironmentVariable(InstallModeEnvironment),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public string ReadInstallToken()
    {
        var token = Environment.GetEnvironmentVariable(InstallTokenEnvironment);
        if (string.IsNullOrWhiteSpace(token) && File.Exists(InstallTokenPath))
        {
            token = File.ReadAllText(InstallTokenPath).Trim();
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
        {
            throw new InvalidOperationException(
                "Install mode requires NETRELAY_INSTALL_TOKEN or a protected config/install.token file.");
        }

        return token;
    }

    public bool VerifyInstallToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var expected = ReadInstallToken();
        if (expected.Length != token.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(token));
    }
}
