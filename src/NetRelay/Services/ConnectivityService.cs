using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class ConnectivityService
{
    public async Task<ConnectivityResult> ProbeAdapterAsync(string adapterId, ConnectivityProbePolicy policy)
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
                "PROBE_POLICY_INVALID"
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
                "ADAPTER_NOT_FOUND"
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
                "ADAPTER_OPERATION_FAILED"
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
                "PROBE_ROUTE_UNAVAILABLE"
            );
        }

        // 优先选择 IPv4 地址进行探测
        var localIp = unicastAddresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            ?? unicastAddresses.First();
        var routePresent = HasDefaultGateway(adapter);

        // 3. 执行多轮探测逻辑
        int failedRounds = 0;

        for (int i = 0; i < policy.Attempts; i++)
        {
            if (i > 0)
            {
                await Task.Delay(500); // 每轮间隔 500ms
            }

            var roundAttempts = new List<ProbeAttempt>();
            var successfulEndpoints = 0;

            foreach (var endpoint in policy.Endpoints)
            {
                var attempt = await TryProbeEndpointAsync(endpoint, localIp, policy.Timeout);
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

        return new ConnectivityResult(
            adapterId,
            checkedAt,
            isOnline,
            RoutePresent: routePresent,
            attempts,
            reasonCode
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
        TimeSpan timeout)
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

                        // 解析 DNS
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

            var response = await client.GetAsync(endpoint.Url);
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
}
