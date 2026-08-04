using System.Security.Cryptography;
using NetRelay.Contracts;
using NetRelay.Contracts.Security;
using Omnexa.Core;
using Omnexa.Sdk;

namespace NetRelay.Services;

public sealed class PolicyService
{
    private readonly ConfigurationService _configService;
    private readonly OmnexaIntegrationService _omnexa;

    public bool IsBlocked { get; private set; }
    public bool IsRestricted { get; private set; }
    public bool IsMaintenance { get; private set; }
    public bool IsMandatoryUpdateRequired { get; private set; }
    public string Decision { get; private set; } = "restricted";
    public string? Reason { get; private set; } = "尚未完成 Omnexa 云控同步。";
    public string? AppealUrl { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public bool AllowUpdate { get; private set; } = true;
    public int NextSyncSeconds { get; private set; } = 300;
    public ControlSnapshot? CurrentControl { get; private set; }
    public ControlSnapshotSource CurrentControlSource { get; private set; } = ControlSnapshotSource.CachedOffline;

    public event EventHandler? BlockStateChanged;
    public event EventHandler<ControlSnapshot>? ControlSynchronized;

    public PolicyService(ConfigurationService configService)
    {
        _configService = configService;
        _omnexa = new OmnexaIntegrationService(configService);
    }

    public async Task CheckPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (!await _omnexa.HasCachedActivationAsync(cancellationToken))
        {
            var activation = new ActivationService(_configService);
            if (!await activation.ActivateAsync(cancellationToken))
            {
                throw new InvalidOperationException("Omnexa 安装激活失败。");
            }
        }

        ControlSyncResult syncResult;
        try
        {
            syncResult = await _omnexa.SyncWithSourceAsync(cancellationToken);
        }
        catch (OmnexaApiException exception) when (
            exception.Code is "INSTALLATION_REQUIRED" or "ACTIVATION_RECEIPT_INVALID")
        {
            var activation = new ActivationService(_configService);
            if (!await activation.ActivateAsync(cancellationToken))
            {
                throw;
            }

            syncResult = await _omnexa.SyncWithSourceAsync(cancellationToken);
        }

        CurrentControlSource = syncResult.Source;
        ApplyControl(syncResult.Snapshot);
    }

    public TimeSpan GetNextSyncDelay()
    {
        var seconds = Math.Clamp(NextSyncSeconds, 60, 24 * 60 * 60);
        var jitter = 0.9 + Random.Shared.NextDouble() * 0.2;
        return TimeSpan.FromSeconds(seconds * jitter);
    }

    private void ApplyControl(ControlSnapshot control)
    {
        var wasBlocked = IsBlocked;
        var wasRestricted = IsRestricted;
        var wasMaintenance = IsMaintenance;
        var wasMandatoryUpdateRequired = IsMandatoryUpdateRequired;
        var oldReason = Reason;

        var decision = control.Decision.Trim().ToLowerInvariant();
        if (decision is not ("allow" or "restricted" or "deny"))
        {
            decision = "restricted";
        }

        CurrentControl = control;
        Decision = decision;
        IsRestricted = decision == "restricted";
        IsMaintenance = control.Maintenance;
        IsMandatoryUpdateRequired = control.Release?.IsMandatory == true &&
                                    ReleaseVersionPolicy.IsStrictlyNewer(
                                        control.Release.Version,
                                        Protocol.ProductVersion);
        IsBlocked = decision is "restricted" or "deny" ||
                    IsMaintenance ||
                    IsMandatoryUpdateRequired;
        Reason = BuildReason(control, decision, IsMandatoryUpdateRequired);
        AppealUrl = null;
        ExpiresAt = control.BanExpiresAt;

        // Omnexa keeps release checks available in restricted and denied modes.
        AllowUpdate = true;
        NextSyncSeconds = Math.Clamp(control.NextSyncSeconds, 60, 24 * 60 * 60);

        if (_configService.Current.PersistedBlockState is not null)
        {
            // Remove the legacy NetRelay-server policy cache. Omnexa owns the
            // independently verified last-known-good snapshot in its cache.
            _configService.Current.PersistedBlockState = null;
            _configService.Save();
        }

        if (wasBlocked != IsBlocked ||
            wasRestricted != IsRestricted ||
            wasMaintenance != IsMaintenance ||
            wasMandatoryUpdateRequired != IsMandatoryUpdateRequired ||
            !string.Equals(oldReason, Reason, StringComparison.Ordinal))
        {
            BlockStateChanged?.Invoke(this, EventArgs.Empty);
        }

        ControlSynchronized?.Invoke(this, control);
    }

    private static string? BuildReason(
        ControlSnapshot control,
        string decision,
        bool mandatoryUpdateRequired)
    {
        if (mandatoryUpdateRequired)
        {
            return $"Omnexa 已发布必须安装的 NetRelay {control.Release!.Version}。" +
                   "请先完成更新，再继续使用网络切换和自动化功能。";
        }

        if (control.Maintenance)
        {
            return string.IsNullOrWhiteSpace(control.Reason)
                ? "Omnexa 正在维护此环境，NetRelay 已进入受限模式。"
                : control.Reason;
        }

        return decision switch
        {
            "deny" => string.IsNullOrWhiteSpace(control.Reason)
                ? "Omnexa 已拒绝此安装继续正常运行。"
                : control.Reason,
            "restricted" => string.IsNullOrWhiteSpace(control.Reason)
                ? "Omnexa 要求 NetRelay 进入受限模式。"
                : control.Reason,
            _ => control.Reason
        };
    }

    // Retained only for validating legacy cached policies during migration tests.
    // New runtime decisions are verified by Omnexa.Sdk and never use this path.
    public bool VerifyStateEnvelope(PolicyEvaluateResponse response)
    {
        try
        {
            using var rootKey = ECDsa.Create();
            rootKey.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(
                    NetRelay.Contracts.Security.OperationalKeyCertificate
                        .DefaultRootPublicKeyBase64),
                out _);

            if (!response.Certificate.Verify(
                    DateTimeOffset.UtcNow,
                    rootKey,
                    "policy"))
            {
                return false;
            }

            using var operationKey = ECDsa.Create();
            operationKey.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(response.Certificate.PublicKey),
                out _);
            return response.Envelope.Verify(
                "policy",
                response.Envelope.Nonce,
                DateTimeOffset.UtcNow,
                operationKey);
        }
        catch
        {
            return false;
        }
    }
}
