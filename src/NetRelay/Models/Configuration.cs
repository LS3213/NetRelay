using System.Text.Json.Serialization;

namespace NetRelay.Models;

public sealed class AppConfiguration
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 2;

    [JsonPropertyName("probePolicy")]
    public ConnectivityProbePolicy ProbePolicy { get; set; } = new();

    [JsonPropertyName("rules")]
    public List<AutomationRule> Rules { get; set; } = [];

    [JsonPropertyName("closeAction")]
    public string CloseAction { get; set; } = "Ask"; // Ask, HideToTray, Exit

    [JsonPropertyName("doNotRemindClose")]
    public bool DoNotRemindClose { get; set; } = false;

    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; set; } = false;

    [JsonPropertyName("manualDisableProtection")]
    public bool ManualDisableProtection { get; set; } = true;

    [JsonPropertyName("debounceSeconds")]
    public int DebounceSeconds { get; set; } = 10;

    [JsonPropertyName("cooldownMinutes")]
    public int CooldownMinutes { get; set; } = 5;

    [JsonPropertyName("keepDays")]
    public int KeepDays { get; set; } = 30;

    [JsonPropertyName("privacyConsentAccepted")]
    public bool PrivacyConsentAccepted { get; set; } = false;

    [JsonPropertyName("acceptedTermsVersion")]
    public string AcceptedTermsVersion { get; set; } = string.Empty;

    [JsonPropertyName("acceptedPrivacyVersion")]
    public string AcceptedPrivacyVersion { get; set; } = string.Empty;

    [JsonPropertyName("privacyConsentTimestamp")]
    public DateTimeOffset? PrivacyConsentTimestamp { get; set; }

    [JsonPropertyName("installationId")]
    public string InstallationId { get; set; } = string.Empty;

    [JsonPropertyName("machineCode")]
    public string MachineCode { get; set; } = string.Empty;

    [JsonPropertyName("activationReceipt")]
    public string ActivationReceipt { get; set; } = string.Empty;

    [JsonPropertyName("lastHeartbeatTimestamp")]
    public DateTimeOffset? LastHeartbeatTimestamp { get; set; }

    [JsonPropertyName("clientConfigurationVersion")]
    public int ClientConfigurationVersion { get; set; } = 0;

    [JsonPropertyName("primaryApiBaseUrl")]
    public string PrimaryApiBaseUrl { get; set; } = "https://netrelay.lansil.cn";

    [JsonPropertyName("githubRepository")]
    public string GithubRepository { get; set; } = "lansi/NetRelay";

    [JsonPropertyName("updateChannel")]
    public string UpdateChannel { get; set; } = "stable";

    [JsonPropertyName("allowGithubFallback")]
    public bool AllowGithubFallback { get; set; } = true;

    [JsonPropertyName("displayedAnnouncementIds")]
    public List<string> DisplayedAnnouncementIds { get; set; } = [];
}

public sealed class ConnectivityProbePolicy
{
    [JsonPropertyName("endpoints")]
    public List<ProbeEndpoint> Endpoints { get; set; } =
    [
        new() { Url = "http://www.msftconnecttest.com/connecttest.txt", ExpectedContent = "Microsoft Connect Test" },
        new() { Url = "https://www.baidu.com", ExpectedContent = null }
    ];

    [JsonPropertyName("timeoutSeconds")]
    public double TimeoutSeconds { get; set; } = 3.0;

    [JsonIgnore]
    public TimeSpan Timeout
    {
        get => TimeSpan.FromSeconds(TimeoutSeconds);
        set => TimeoutSeconds = value.TotalSeconds;
    }

    [JsonPropertyName("attempts")]
    public int Attempts { get; set; } = 3;

    [JsonPropertyName("requiredFailedAttempts")]
    public int RequiredFailedAttempts { get; set; } = 2;

    [JsonPropertyName("requiredSuccessfulEndpoints")]
    public int RequiredSuccessfulEndpoints { get; set; } = 1;

    [JsonPropertyName("optionalPingDiagnostics")]
    public bool OptionalPingDiagnostics { get; set; } = false;
}

public sealed class ProbeEndpoint
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("expectedContent")]
    public string? ExpectedContent { get; set; }
}
