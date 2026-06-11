using System.Net.NetworkInformation;
using NetRelay.Infrastructure;

namespace NetRelay.Models;

public sealed class NetworkAdapterInfo : ObservableObject
{
    private const int HistoryLimit = 32;
    private readonly List<double> _trafficHistory = [];
    private long? _lastBytesReceived;
    private long? _lastBytesSent;
    private DateTimeOffset? _lastSampledAt;
    private double _receiveBytesPerSecond;
    private double _sendBytesPerSecond;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required NetworkInterfaceType InterfaceType { get; init; }
    public required OperationalStatus OperationalStatus { get; init; }
    public required long Speed { get; init; }
    public required string MacAddress { get; init; }
    public required IReadOnlyList<string> IpAddresses { get; init; }
    public required bool IsLikelyVirtual { get; init; }
    public required string ClassificationLabel { get; init; }

    public bool IsConnected => OperationalStatus == OperationalStatus.Up;
    public bool IsEnabled => OperationalStatus is not OperationalStatus.NotPresent;
    public string TypeLabel => InterfaceType switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "有线网络",
        NetworkInterfaceType.Wireless80211 => "无线网络",
        NetworkInterfaceType.Ppp => "VPN / PPP",
        NetworkInterfaceType.Loopback => "环回接口",
        NetworkInterfaceType.Tunnel => "隧道接口",
        _ => "其他网卡"
    };
    public string StatusLabel => IsConnected ? "已连接" : IsEnabled ? "未连接" : "已禁用";
    public string PrimaryIpAddress => IpAddresses.FirstOrDefault() ?? "未分配";
    public string SpeedLabel => Speed <= 0 ? "未知" : $"{Speed / 1_000_000d:0.#} Mbps";
    public IReadOnlyList<double> TrafficHistory => _trafficHistory;
    public double ReceiveBytesPerSecond
    {
        get => _receiveBytesPerSecond;
        private set => SetProperty(ref _receiveBytesPerSecond, value);
    }
    public double SendBytesPerSecond
    {
        get => _sendBytesPerSecond;
        private set => SetProperty(ref _sendBytesPerSecond, value);
    }
    public double TotalBytesPerSecond => ReceiveBytesPerSecond + SendBytesPerSecond;
    public bool HasTraffic => TotalBytesPerSecond >= 128;
    public string TrafficRateLabel => TotalBytesPerSecond < 1
        ? "无活动"
        : $"↓ {FormatRate(ReceiveBytesPerSecond)}  ↑ {FormatRate(SendBytesPerSecond)}";

    public void UpdateTraffic(long bytesReceived, long bytesSent, DateTimeOffset sampledAt)
    {
        if (_lastBytesReceived is long previousReceived
            && _lastBytesSent is long previousSent
            && _lastSampledAt is DateTimeOffset previousSampledAt)
        {
            var elapsedSeconds = Math.Max((sampledAt - previousSampledAt).TotalSeconds, 0.001);
            ReceiveBytesPerSecond = Math.Max(0, bytesReceived - previousReceived) / elapsedSeconds;
            SendBytesPerSecond = Math.Max(0, bytesSent - previousSent) / elapsedSeconds;

            _trafficHistory.Add(TotalBytesPerSecond);
            if (_trafficHistory.Count > HistoryLimit)
            {
                _trafficHistory.RemoveAt(0);
            }

            RaisePropertyChanged(nameof(TotalBytesPerSecond));
            RaisePropertyChanged(nameof(HasTraffic));
            RaisePropertyChanged(nameof(TrafficRateLabel));
            RaisePropertyChanged(nameof(TrafficHistory));
        }

        _lastBytesReceived = bytesReceived;
        _lastBytesSent = bytesSent;
        _lastSampledAt = sampledAt;
    }

    private static string FormatRate(double bytesPerSecond)
    {
        return bytesPerSecond switch
        {
            >= 1_048_576 => $"{bytesPerSecond / 1_048_576:0.0} MB/s",
            >= 1_024 => $"{bytesPerSecond / 1_024:0.0} KB/s",
            _ => $"{bytesPerSecond:0} B/s"
        };
    }
}
