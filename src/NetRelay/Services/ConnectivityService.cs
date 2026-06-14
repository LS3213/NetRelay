using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NetRelay.Models;
using NetRelay.Native;
using NetRelay.Contracts;
using NetRelay.Contracts.Security;

namespace NetRelay.Services;

public sealed class ConnectivityService
{
    public async Task<ConnectivityResult> ProbeAdapterAsync(string adapterId, ConnectivityProbePolicy policy, CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.Now;
        var attempts = new List<ProbeAttempt>();

        if (ConnectivityProbePolicyValidator.Validate(policy) is not null)
        {
            return new ConnectivityResult(
                adapterId,
                checkedAt,
                Online: false,
                RoutePresent: false,
                attempts,
                "PROBE_POLICY_INVALID",
                "Disconnected"
            );
        }

        // 1. 查找目标适配器
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(ni => string.Equals(ni.Id, adapterId, StringComparison.OrdinalIgnoreCase));

        if (adapter is null)
        {
            return new ConnectivityResult(
                adapterId,
                checkedAt,
                Online: false,
                RoutePresent: false,
                attempts,
                "ADAPTER_NOT_FOUND",
                "Disconnected"
            );
        }

        if (adapter.OperationalStatus != OperationalStatus.Up)
        {
            return new ConnectivityResult(
                adapterId,
                checkedAt,
                Online: false,
                RoutePresent: false,
                attempts,
                "ADAPTER_OPERATION_FAILED",
                "Disconnected"
            );
        }

        // 2. 获取网卡绑定的单播 IP 地址
        List<IPAddress> unicastAddresses;
        try
        {
            var ipProperties = adapter.GetIPProperties();
            unicastAddresses = ipProperties.UnicastAddresses
                .Select(ua => ua.Address)
                .Where(ip => ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            unicastAddresses = [];
        }

        if (unicastAddresses.Count == 0)
        {
            return new ConnectivityResult(
                adapterId,
                checkedAt,
                Online: false,
                RoutePresent: false,
                attempts,
                "PROBE_ROUTE_UNAVAILABLE",
                "Disconnected"
            );
        }

        // 优先选择 IPv4 地址进行探测
        var localIp = unicastAddresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            ?? unicastAddresses.First();
        var routePresent = HasDefaultGateway(adapter);

        // 3. 读取 NLM/NCSI 辅助联网状态
        string? nlmConnectivity = GetNlmConnectivityForAdapter(adapterId);

        // 4. 执行多轮探测逻辑
        int failedRounds = 0;

        for (int i = 0; i < policy.Attempts; i++)
        {
            if (i > 0)
            {
                await Task.Delay(500, cancellationToken); // 每轮间隔 500ms
            }

            var roundAttempts = new List<ProbeAttempt>();
            var successfulEndpoints = 0;

            foreach (var endpoint in policy.Endpoints)
            {
                var attempt = await TryProbeEndpointAsync(endpoint, localIp, policy.Timeout, cancellationToken);
                roundAttempts.Add(attempt);
                attempts.Add(attempt);

                if (attempt.Success)
                {
                    successfulEndpoints++;
                }
            }

            if (successfulEndpoints < policy.RequiredSuccessfulEndpoints)
            {
                failedRounds++;
            }
        }

        bool isOnline = failedRounds < policy.RequiredFailedAttempts;
        string reasonCode = isOnline
            ? "ONLINE"
            : routePresent
                ? "PROBE_FAILED"
                : "PROBE_ROUTE_UNAVAILABLE";

        // 执行后端挑战验证 (仅在已配置数据目录和激活的情况下执行，这里总是尝试进行以作为高可信信号)
        var challengeAttempt = await TryProbeChallengeAsync(localIp, policy.Timeout, cancellationToken);
        attempts.Add(challengeAttempt);

        if (challengeAttempt.Success)
        {
            isOnline = true;
            reasonCode = "ONLINE";
        }
        else if (challengeAttempt.ErrorMessage != null && challengeAttempt.ErrorMessage.StartsWith("BACKEND_ERROR", StringComparison.Ordinal))
        {
            // 如果后端故障 (如 503 等)，我们优雅降级：不判定断网，但将原因修改为“后端验证不可用”
            if (isOnline)
            {
                reasonCode = "BACKEND_UNAVAILABLE";
            }
        }

        return new ConnectivityResult(
            adapterId,
            checkedAt,
            isOnline,
            RoutePresent: routePresent,
            attempts,
            reasonCode,
            nlmConnectivity
        );
    }

    private static bool HasDefaultGateway(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses.Any(gateway =>
                !gateway.Address.Equals(IPAddress.Any)
                && !gateway.Address.Equals(IPAddress.IPv6Any)
                && !gateway.Address.Equals(IPAddress.None)
                && !gateway.Address.Equals(IPAddress.IPv6None));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static async Task<ProbeAttempt> TryProbeEndpointAsync(
        ProbeEndpoint endpoint,
        IPAddress localIp,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri))
        {
            return new ProbeAttempt(endpoint.Url, false, 0, "端点 URL 格式不合法。");
        }

        if (uri.Scheme == "ping")
        {
            return await TryProbePingAsync(uri, localIp, timeout, cancellationToken);
        }
        else if (uri.Scheme == "dns")
        {
            return await TryProbeDnsAsync(uri, localIp, timeout, cancellationToken);
        }
        else
        {
            return await TryProbeHttpAsync(endpoint, localIp, timeout, cancellationToken);
        }
    }

    private static async Task<ProbeAttempt> TryProbePingAsync(
        Uri uri,
        IPAddress localIp,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var host = uri.Host;

        try
        {
            // 1. 解析目标 IP (以匹配本地 IP 协议族)
            var ips = await Dns.GetHostAddressesAsync(host, cancellationToken);
            var targetIp = ips.FirstOrDefault(ip => ip.AddressFamily == localIp.AddressFamily)
                ?? throw new SocketException((int)SocketError.AddressFamilyNotSupported);

            // 2. 调用系统自带 ping.exe 执行绑定网卡的 ICMP 探测
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ping.exe",
                    Arguments = $"-S {localIp} {targetIp} -n 1 -w {(int)timeout.TotalMilliseconds}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            bool success = process.ExitCode == 0;
            long elapsed = stopwatch.ElapsedMilliseconds;

            if (success)
            {
                return new ProbeAttempt(uri.OriginalString, true, elapsed, null);
            }
            else
            {
                return new ProbeAttempt(uri.OriginalString, false, elapsed, "Ping 探测无回复或超时。");
            }
        }
        catch (OperationCanceledException)
        {
            return new ProbeAttempt(uri.OriginalString, false, stopwatch.ElapsedMilliseconds, "Ping 探测请求被取消或超时。");
        }
        catch (Exception ex)
        {
            return new ProbeAttempt(uri.OriginalString, false, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }

    private static async Task<ProbeAttempt> TryProbeDnsAsync(
        Uri uri,
        IPAddress localIp,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var domain = uri.Host;

        try
        {
            // 1. 获取该网卡配的 DNS 服务器
            var dnsServers = new List<IPAddress>();
            try
            {
                var adapter = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(ni => ni.GetIPProperties().UnicastAddresses
                        .Any(ua => ua.Address.Equals(localIp)));
                if (adapter != null)
                {
                    dnsServers.AddRange(adapter.GetIPProperties().DnsAddresses
                        .Where(ip => ip.AddressFamily == localIp.AddressFamily));
                }
            }
            catch
            {
                // 忽略异常
            }

            // 2. Fallback 到公共 DNS
            if (dnsServers.Count == 0)
            {
                if (localIp.AddressFamily == AddressFamily.InterNetwork)
                {
                    dnsServers.Add(IPAddress.Parse("223.5.5.5"));
                    dnsServers.Add(IPAddress.Parse("114.114.114.114"));
                }
                else
                {
                    dnsServers.Add(IPAddress.Parse("2400:3200::1"));
                    dnsServers.Add(IPAddress.Parse("2001:4860:4860::8888"));
                }
            }

            // 3. 构建 DNS 查询数据包并执行 UDP 探测
            var queryPacket = BuildDnsQuery(domain, localIp.AddressFamily == AddressFamily.InterNetworkV6);
            IPAddress? resolvedIp = null;
            var lastDnsError = "DNS 探测失败，无 DNS 响应回复。";

            foreach (var dnsServer in dnsServers)
            {
                try
                {
                    using var socket = new Socket(localIp.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    socket.Bind(new IPEndPoint(localIp, 0));

                    var targetEndPoint = new IPEndPoint(dnsServer, 53);
                    await socket.SendToAsync(queryPacket, SocketFlags.None, targetEndPoint, cancellationToken);

                    var buffer = new byte[512];
                    var receiveTask = socket.ReceiveFromAsync(buffer, SocketFlags.None, targetEndPoint);

                    if (await Task.WhenAny(receiveTask, Task.Delay(timeout, cancellationToken)) == receiveTask)
                    {
                        var receiveResult = await receiveTask;
                        if (!targetEndPoint.Equals(receiveResult.RemoteEndPoint))
                        {
                            lastDnsError = "DNS 响应来源与请求服务器不匹配。";
                            continue;
                        }

                        if (TryParseDnsResponse(
                                buffer,
                                receiveResult.ReceivedBytes,
                                queryPacket,
                                localIp.AddressFamily,
                                out resolvedIp,
                                out lastDnsError))
                        {
                            break;
                        }
                    }
                    else
                    {
                        lastDnsError = "DNS 探测请求超时。";
                    }
                }
                catch
                {
                    // 尝试下一个服务器
                }
            }

            long elapsed = stopwatch.ElapsedMilliseconds;

            if (resolvedIp is not null)
            {
                return new ProbeAttempt(uri.OriginalString, true, elapsed, null);
            }

            return new ProbeAttempt(uri.OriginalString, false, elapsed, lastDnsError);
        }
        catch (OperationCanceledException)
        {
            return new ProbeAttempt(uri.OriginalString, false, stopwatch.ElapsedMilliseconds, "DNS 探测请求被取消或超时。");
        }
        catch (Exception ex)
        {
            return new ProbeAttempt(uri.OriginalString, false, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }

    private static async Task<ProbeAttempt> TryProbeHttpAsync(
        ProbeEndpoint endpoint,
        IPAddress localIp,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(localIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };

                    try
                    {
                        socket.Bind(new IPEndPoint(localIp, 0));

                        var ips = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                        var targetIp = ips.FirstOrDefault(ip => ip.AddressFamily == localIp.AddressFamily)
                            ?? throw new SocketException((int)SocketError.AddressFamilyNotSupported);

                        await socket.ConnectAsync(new IPEndPoint(targetIp, context.DnsEndPoint.Port), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };

            using var client = new HttpClient(handler)
            {
                Timeout = timeout
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) NetRelay/1.0");

            var response = await client.GetAsync(endpoint.Url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ProbeAttempt(
                    endpoint.Url,
                    Success: false,
                    stopwatch.ElapsedMilliseconds,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
                );
            }

            if (endpoint.ExpectedContent != null)
            {
                var content = await response.Content.ReadAsStringAsync();
                if (!content.Contains(endpoint.ExpectedContent, StringComparison.OrdinalIgnoreCase))
                {
                    return new ProbeAttempt(
                        endpoint.Url,
                        Success: false,
                        stopwatch.ElapsedMilliseconds,
                        "响应内容不匹配。"
                    );
                }
            }

            return new ProbeAttempt(
                endpoint.Url,
                Success: true,
                stopwatch.ElapsedMilliseconds,
                null
            );
        }
        catch (OperationCanceledException)
        {
            return new ProbeAttempt(
                endpoint.Url,
                Success: false,
                stopwatch.ElapsedMilliseconds,
                "请求超时。"
            );
        }
        catch (Exception exception)
        {
            return new ProbeAttempt(
                endpoint.Url,
                Success: false,
                stopwatch.ElapsedMilliseconds,
                exception.Message
            );
        }
    }

    private static byte[] BuildDnsQuery(string domain, bool isIpv6)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        var transactionId = RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        writer.Write((byte)(transactionId >> 8));
        writer.Write((byte)transactionId);
        writer.Write((byte)0x01);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        writer.Write((byte)0x00); writer.Write((byte)0x00);
        writer.Write((byte)0x00); writer.Write((byte)0x00);
        writer.Write((byte)0x00); writer.Write((byte)0x00);

        var parts = domain.Split('.');
        foreach (var part in parts)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(part);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }
        writer.Write((byte)0);

        writer.Write((byte)0x00);
        writer.Write((byte)(isIpv6 ? 0x1c : 0x01));
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);

        return stream.ToArray();
    }

    private static bool TryParseDnsResponse(
        byte[] buffer,
        int length,
        byte[] queryPacket,
        AddressFamily family,
        out IPAddress? resolvedIp,
        out string errorMessage)
    {
        resolvedIp = null;
        errorMessage = "DNS 响应格式无效。";

        try
        {
            if (length < 12 || length > buffer.Length || queryPacket.Length < 12)
            {
                return false;
            }

            if (buffer[0] != queryPacket[0] || buffer[1] != queryPacket[1])
            {
                errorMessage = "DNS 响应事务 ID 与请求不匹配。";
                return false;
            }

            var flags = (ushort)((buffer[2] << 8) | buffer[3]);
            if ((flags & 0x8000) == 0)
            {
                errorMessage = "DNS 数据包不是响应。";
                return false;
            }

            if ((flags & 0x0200) != 0)
            {
                errorMessage = "DNS UDP 响应已截断。";
                return false;
            }

            var responseCode = flags & 0x000F;
            if (responseCode != 0)
            {
                errorMessage = $"DNS 服务器返回错误码 {responseCode}。";
                return false;
            }

            int questions = (buffer[4] << 8) | buffer[5];
            if (questions == 0)
            {
                errorMessage = "DNS 响应不包含问题段。";
                return false;
            }

            int index = 12;
            for (int q = 0; q < questions; q++)
            {
                if (!TrySkipDnsName(buffer, index, length, out index) || index + 4 > length)
                {
                    return false;
                }

                index += 4;
            }

            int answers = (buffer[6] << 8) | buffer[7];
            if (answers == 0)
            {
                errorMessage = "DNS 响应不包含有效答案。";
                return false;
            }

            for (int a = 0; a < answers && index < length; a++)
            {
                if (!TrySkipDnsName(buffer, index, length, out index) || index + 10 > length)
                {
                    return false;
                }

                ushort type = (ushort)((buffer[index] << 8) | buffer[index + 1]);
                index += 2; // Type
                ushort recordClass = (ushort)((buffer[index] << 8) | buffer[index + 1]);
                index += 2; // Class
                index += 4; // TTL
                ushort dataLen = (ushort)((buffer[index] << 8) | buffer[index + 1]);
                index += 2; // Data Length

                if (index + dataLen > length)
                {
                    return false;
                }

                if (recordClass == 1 && type == 1 && family == AddressFamily.InterNetwork && dataLen == 4)
                {
                    var ipBytes = new byte[4];
                    Array.Copy(buffer, index, ipBytes, 0, 4);
                    resolvedIp = new IPAddress(ipBytes);
                    errorMessage = string.Empty;
                    return true;
                }

                if (recordClass == 1 && type == 28 && family == AddressFamily.InterNetworkV6 && dataLen == 16)
                {
                    var ipBytes = new byte[16];
                    Array.Copy(buffer, index, ipBytes, 0, 16);
                    resolvedIp = new IPAddress(ipBytes);
                    errorMessage = string.Empty;
                    return true;
                }

                index += dataLen;
            }

            errorMessage = family == AddressFamily.InterNetworkV6
                ? "DNS 响应不包含有效 AAAA 地址。"
                : "DNS 响应不包含有效 A 地址。";
        }
        catch
        {
            errorMessage = "DNS 响应解析失败。";
        }

        return false;
    }

    private static bool TrySkipDnsName(byte[] buffer, int index, int length, out int nextIndex)
    {
        nextIndex = index;

        while (index < length)
        {
            byte len = buffer[index];
            if (len == 0)
            {
                nextIndex = index + 1;
                return true;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (index + 1 >= length)
                {
                    return false;
                }

                var pointer = ((len & 0x3F) << 8) | buffer[index + 1];
                if (pointer >= length)
                {
                    return false;
                }

                nextIndex = index + 2;
                return true;
            }

            if (len > 63 || index + 1 + len > length)
            {
                return false;
            }

            index += 1 + len;
        }

        return false;
    }

    private static string? GetNlmConnectivityForAdapter(string adapterId)
    {
        if (!Guid.TryParse(adapterId, out var adapterGuid))
        {
            return null;
        }

        try
        {
            var nlmType = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (nlmType == null) return "Unknown";

            var nlm = Activator.CreateInstance(nlmType) as INetworkListManager;
            if (nlm == null) return "Unknown";

            try
            {
                var connectionsEnumObj = nlm.GetNetworkConnections();
                if (connectionsEnumObj is IEnumNetworkConnections connectionsEnum)
                {
                    try
                    {
                        var connectionArray = new INetworkConnection[1];
                        INetworkConnection? connection = null;
                        uint fetched = 0;

                        while (connectionsEnum.Next(1, out connection, out fetched) == 0 && fetched == 1 && connection != null)
                        {
                            try
                            {
                                var connAdapterId = connection.GetAdapterId();
                                if (connAdapterId == adapterGuid)
                                {
                                    var connectivity = connection.GetConnectivity();
                                    var flags = (NlmConnectivity)connectivity;
                                    var results = new List<string>();

                                    if (flags.HasFlag(NlmConnectivity.IPv4Internet) || flags.HasFlag(NlmConnectivity.IPv6Internet))
                                        results.Add("Internet");
                                    if (flags.HasFlag(NlmConnectivity.IPv4LocalNetwork) || flags.HasFlag(NlmConnectivity.IPv6LocalNetwork))
                                        results.Add("LocalNetwork");
                                    if (flags.HasFlag(NlmConnectivity.IPv4Subnet) || flags.HasFlag(NlmConnectivity.IPv6Subnet))
                                        results.Add("Subnet");
                                    if (flags.HasFlag(NlmConnectivity.IPv4NoTraffic) || flags.HasFlag(NlmConnectivity.IPv6NoTraffic))
                                        results.Add("NoTraffic");
                                    if (flags.HasFlag(NlmConnectivity.Disconnected))
                                        results.Add("Disconnected");

                                    if (results.Count > 0)
                                    {
                                        return string.Join("|", results);
                                    }
                                    return "Unknown";
                                }
                            }
                            catch
                            {
                                // Ignore
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(connection);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(connectionsEnum);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(nlm);
            }
        }
        catch
        {
            // Ignore
        }

        return "Disconnected";
    }

    private static async Task<ProbeAttempt> TryProbeChallengeAsync(
        IPAddress localIp,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var backendUrl = ActivationService.GetBackendUrl();
        var url = backendUrl + "/api/v1/connectivity/challenge";
        var nonce = Guid.NewGuid().ToString("N");

        try
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(localIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };

                    try
                    {
                        socket.Bind(new IPEndPoint(localIp, 0));

                        var ips = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                        var targetIp = ips.FirstOrDefault(ip => ip.AddressFamily == localIp.AddressFamily)
                            ?? throw new SocketException((int)SocketError.AddressFamilyNotSupported);

                        await socket.ConnectAsync(new IPEndPoint(targetIp, context.DnsEndPoint.Port), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };

            var ignoreSsl = ConfigurationService.Instance?.Current?.IgnoreSslErrors == true;
            if (ignoreSsl)
            {
                handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
                };
            }

            using var client = new HttpClient(handler)
            {
                Timeout = timeout
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) NetRelay/1.0");
            client.DefaultRequestHeaders.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
            client.DefaultRequestHeaders.Add(Protocol.ClientVersionHeader, Protocol.ProductVersion);
            client.DefaultRequestHeaders.Add(Protocol.RequestIdHeader, Guid.NewGuid().ToString("N"));

            var response = await client.PostAsJsonAsync(url, new ConnectivityChallengeRequest { Nonce = nonce }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ProbeAttempt(
                    url,
                    Success: false,
                    stopwatch.ElapsedMilliseconds,
                    $"BACKEND_ERROR: HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
                );
            }

            var apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse<ConnectivityChallengeResponse>>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            if (apiResponse?.Data == null)
            {
                return new ProbeAttempt(url, Success: false, stopwatch.ElapsedMilliseconds, "BACKEND_ERROR: 响应数据为空。");
            }

            var challengeResponse = apiResponse.Data;
            var now = DateTimeOffset.Now;

            // 验证在线证书
            using var rootKey = ECDsa.Create();
            rootKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(OperationalKeyCertificate.DefaultRootPublicKeyBase64), out _);

            if (!challengeResponse.Certificate.Verify(now, rootKey, "connectivity-challenge"))
            {
                return new ProbeAttempt(url, Success: false, stopwatch.ElapsedMilliseconds, "BACKEND_ERROR: 证书校验失败。");
            }

            // 验证挑战签名
            using var operationalKey = ECDsa.Create();
            operationalKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(challengeResponse.Certificate.PublicKey), out _);

            if (!challengeResponse.Envelope.Verify("connectivity-challenge", challengeResponse.Envelope.Nonce, now, operationalKey))
            {
                return new ProbeAttempt(url, Success: false, stopwatch.ElapsedMilliseconds, "BACKEND_ERROR: 挑战签名校验失败。");
            }

            // 校验 nonce 匹配
            var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(challengeResponse.Envelope.PayloadJson);
            if (payload == null || !payload.TryGetValue("nonce", out var returnedNonce) || !string.Equals(returnedNonce, nonce, StringComparison.Ordinal))
            {
                return new ProbeAttempt(url, Success: false, stopwatch.ElapsedMilliseconds, "BACKEND_ERROR: Nonce 不匹配。");
            }

            return new ProbeAttempt(
                url,
                Success: true,
                stopwatch.ElapsedMilliseconds,
                null
            );
        }
        catch (OperationCanceledException)
        {
            return new ProbeAttempt(url, Success: false, stopwatch.ElapsedMilliseconds, "请求超时。");
        }
        catch (Exception exception)
        {
            var errorMsg = exception.Message;
            var isBackendIssue = errorMsg.Contains("BACKEND_ERROR") || exception is CryptographicException || exception is JsonException;
            var reason = isBackendIssue ? "BACKEND_ERROR: " + errorMsg : errorMsg;

            return new ProbeAttempt(
                url,
                Success: false,
                stopwatch.ElapsedMilliseconds,
                reason
            );
        }
    }
}
