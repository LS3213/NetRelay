using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRelay.Contracts;
using NetRelay.Contracts.Security;
using NetRelay.Server.Configuration;
using NetRelay.Server.Data;
using NetRelay.Server.Infrastructure;
using NetRelay.Server.Security;

namespace NetRelay.Server.Services;

public sealed class DeviceActivationService(
    NetRelayDbContext dbContext,
    KeyManagementService keyManagementService,
    IOptions<ServerOptions> options,
    ILogger<DeviceActivationService> logger)
{
    private static readonly string[] CoreCategories =
    [
        "hardware.windowsDeviceId",
        "hardware.machineGuid",
        "hardware.systemUuid",
        "hardware.motherboard",
        "hardware.processor",
        "hardware.systemDrive"
    ];

    private static readonly string[] NetworkCategories =
    [
        "network.physicalCandidate.adapterGuid",
        "network.physicalCandidate.mac",
        "network.physicalCandidate.model"
    ];

    public async Task<DeviceActivationResponse> ActivateDeviceAsync(
        DeviceActivationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.InstallationId == Guid.Empty)
        {
            throw new ArgumentException("InstallationId 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(request.AcceptedTermsVersion) ||
            string.IsNullOrWhiteSpace(request.AcceptedPrivacyVersion))
        {
            throw new ArgumentException("必须接受服务条款与隐私政策版本。");
        }

        var now = DateTimeOffset.UtcNow;
        var config = options.Value;

        // 1. 扁平化传入的 candidate evidence
        var candidateCore = new HashSet<string>(StringComparer.Ordinal);
        var candidateNetwork = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in request.Evidence)
        {
            if (CoreCategories.Contains(pair.Key, StringComparer.Ordinal))
            {
                foreach (var val in pair.Value) candidateCore.Add(val);
            }
            else if (NetworkCategories.Contains(pair.Key, StringComparer.Ordinal))
            {
                foreach (var val in pair.Value) candidateNetwork.Add(val);
            }
        }

        // 2. 加载数据库中所有的设备证据并进行匹配评分
        var allEvidence = await dbContext.DeviceEvidences
            .Select(e => new { e.DeviceId, e.Category, e.EvidenceHash })
            .ToListAsync(cancellationToken);

        var evidenceByDevice = allEvidence
            .GroupBy(e => e.DeviceId)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Core = g.Where(e => CoreCategories.Contains(e.Category, StringComparer.Ordinal)).Select(e => e.EvidenceHash).ToHashSet(StringComparer.Ordinal),
                    Network = g.Where(e => NetworkCategories.Contains(e.Category, StringComparer.Ordinal)).Select(e => e.EvidenceHash).ToHashSet(StringComparer.Ordinal)
                });

        Guid? matchedDeviceId = null;
        int bestScore = 0;

        foreach (var pair in evidenceByDevice)
        {
            var coreMatches = pair.Value.Core.Intersect(candidateCore).Count();
            var networkMatches = pair.Value.Network.Intersect(candidateNetwork).Count();

            // 核心证据至少匹配 MinCoreEvidenceMatches 件
            if (coreMatches >= config.MinCoreEvidenceMatches)
            {
                var score = (coreMatches * config.CoreEvidenceWeight) + (networkMatches * config.NetworkEvidenceWeight);
                if (score >= config.MinEvidenceScore && score > bestScore)
                {
                    bestScore = score;
                    matchedDeviceId = pair.Key;
                }
            }
        }

        Device device;
        if (matchedDeviceId.HasValue)
        {
            device = (await dbContext.Devices.FindAsync(new object[] { matchedDeviceId.Value }, cancellationToken))!;
            device.LastSeenAt = now;
            logger.LogInformation("设备指纹匹配成功。关联至已有设备 MachineCode: {MachineCode}, 评分: {Score}", device.MachineCode, bestScore);
        }
        else
        {
            device = new Device
            {
                Id = Uuid7.Create(now),
                MachineCode = "MC" + TokenService.CreateToken(16).Substring(0, 16).ToUpperInvariant(),
                DeviceIdHash = request.DeviceId,
                FingerprintVersion = request.FingerprintVersion,
                FirstSeenAt = now,
                LastSeenAt = now
            };
            dbContext.Devices.Add(device);
            logger.LogInformation("无匹配设备，已创建新设备记录。MachineCode: {MachineCode}", device.MachineCode);
        }

        // 3. 更新或保存 DeviceEvidence 记录
        var existingEvidences = matchedDeviceId.HasValue
            ? await dbContext.DeviceEvidences.Where(e => e.DeviceId == device.Id).ToListAsync(cancellationToken)
            : [];

        foreach (var pair in request.Evidence)
        {
            foreach (var val in pair.Value)
            {
                var exists = existingEvidences.Any(e =>
                    string.Equals(e.Category, pair.Key, StringComparison.Ordinal) &&
                    string.Equals(e.EvidenceHash, val, StringComparison.Ordinal));

                if (!exists)
                {
                    dbContext.DeviceEvidences.Add(new DeviceEvidence
                    {
                        Id = Uuid7.Create(now),
                        DeviceId = device.Id,
                        Category = pair.Key,
                        EvidenceHash = val,
                        FirstSeenAt = now,
                        LastSeenAt = now
                    });
                }
                else
                {
                    var item = existingEvidences.First(e =>
                        string.Equals(e.Category, pair.Key, StringComparison.Ordinal) &&
                        string.Equals(e.EvidenceHash, val, StringComparison.Ordinal));
                    item.LastSeenAt = now;
                }
            }
        }

        // 4. 更新或保存 DeviceInstallation 记录
        var installation = await dbContext.DeviceInstallations
            .FirstOrDefaultAsync(i => i.InstallationId == request.InstallationId, cancellationToken);

        if (installation == null)
        {
            installation = new DeviceInstallation
            {
                Id = Uuid7.Create(now),
                DeviceId = device.Id,
                InstallationId = request.InstallationId,
                ClientVersion = request.ClientVersion,
                OsVersion = request.OsVersion,
                ProtocolVersion = request.ProtocolVersion,
                FirstSeenAt = now,
                LastSeenAt = now
            };
            dbContext.DeviceInstallations.Add(installation);
        }
        else
        {
            installation.DeviceId = device.Id;
            installation.ClientVersion = request.ClientVersion;
            installation.OsVersion = request.OsVersion;
            installation.ProtocolVersion = request.ProtocolVersion;
            installation.LastSeenAt = now;
        }

        // 5. 生成并持久化激活回执 (ActivationReceipt)
        var receiptId = Guid.NewGuid();
        var expiresAt = now.AddDays(30);

        var receipt = new ActivationReceipt
        {
            Id = Uuid7.Create(now),
            InstallationId = request.InstallationId,
            ReceiptId = receiptId,
            IssuedAt = now,
            ExpiresAt = expiresAt,
            KeyId = keyManagementService.OperationCertificate.KeyId,
            RevokedAt = null
        };
        dbContext.ActivationReceipts.Add(receipt);

        await dbContext.SaveChangesAsync(cancellationToken);

        // 6. 用当前操作密钥签署激活回执
        var payload = new Dictionary<string, object?>
        {
            ["installationId"] = request.InstallationId.ToString(),
            ["machineCode"] = device.MachineCode,
            ["receiptId"] = receiptId.ToString(),
            ["expiresAt"] = expiresAt.ToString("O")
        };

        var envelope = keyManagementService.Sign(
            "device-activation",
            Guid.NewGuid().ToString("N"),
            now,
            expiresAt,
            payload);

        return new DeviceActivationResponse
        {
            MachineCode = device.MachineCode,
            Envelope = envelope,
            Certificate = keyManagementService.OperationCertificate
        };
    }
}
