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
    private bool _isInternetOnline;
    private DateTimeOffset? _lastProbeTime;
    private string _probeReasonCode = "PENDING";

    private OperationalStatus _operationalStatus;
    private bool _isEnabled;
    private long _speed;
    private string _macAddress = string.Empty;
    private IReadOnlyList<string> _ipAddresses = Array.Empty<string>();
    private string _classificationLabel = string.Empty;
    private bool _canToggle;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required NetworkInterfaceType InterfaceType { get; init; }
    public required bool IsLikelyVirtual { get; init; }

    public required OperationalStatus OperationalStatus
    {
        get => _operationalStatus;
        set
        {
            if (SetProperty(ref _operationalStatus, value))
            {
                RaisePropertyChanged(nameof(IsConnected));
                RaisePropertyChanged(nameof(LinkStatusLabel));
                RaisePropertyChanged(nameof(InternetStatusLabel));
                RaisePropertyChanged(nameof(BadgeStatusLabel));
            }
        }
    }

    public required bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                RaisePropertyChanged(nameof(EnabledStatusLabel));
                RaisePropertyChanged(nameof(LinkStatusLabel));
                RaisePropertyChanged(nameof(InternetStatusLabel));
                RaisePropertyChanged(nameof(BadgeStatusLabel));
            }
        }
    }

    public required long Speed
    {
        get => _speed;
        set
        {
            if (SetProperty(ref _speed, value))
            {
                RaisePropertyChanged(nameof(SpeedLabel));
            }
        }
    }

    public required string MacAddress
    {
        get => _macAddress;
        set => SetProperty(ref _macAddress, value);
    }

    public required IReadOnlyList<string> IpAddresses
    {
        get => _ipAddresses;
        set
        {
            if (SetProperty(ref _ipAddresses, value))
            {
                RaisePropertyChanged(nameof(PrimaryIpAddress));
            }
        }
    }

    public required string ClassificationLabel
    {
        get => _classificationLabel;
        set => SetProperty(ref _classificationLabel, value);
    }

    public required bool CanToggle
    {
        get => _canToggle;
        set => SetProperty(ref _canToggle, value);
    }

    public bool IsConnected => OperationalStatus == OperationalStatus.Up;
    public string TypeLabel => InterfaceType switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "有线网络",
        NetworkInterfaceType.Wireless80211 => "无线网络",
        NetworkInterfaceType.Ppp => "VPN / PPP",
        NetworkInterfaceType.Loopback => "环回接口",
        NetworkInterfaceType.Tunnel => "隧道接口",
        _ => "其他网卡"
    };
    public string EnabledStatusLabel => IsEnabled ? "已启用" : "已禁用";
    public string LinkStatusLabel => !IsEnabled ? "不可用" : IsConnected ? "链路正常" : "链路断开";
    public string BadgeStatusLabel => !IsEnabled
        ? "已禁用"
        : !IsConnected
            ? "链路断开"
            : !LastProbeTime.HasValue
                ? "未检测"
                : IsInternetOnline
                    ? "可联网"
                    : ProbeReasonCode == "PROBE_ROUTE_UNAVAILABLE"
                        ? "仅本地"
                        : "联网失败";
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

    public bool IsInternetOnline
    {
        get => _isInternetOnline;
        set
        {
            if (SetProperty(ref _isInternetOnline, value))
            {
                RaisePropertyChanged(nameof(InternetStatusLabel));
                RaisePropertyChanged(nameof(BadgeStatusLabel));
            }
        }
    }

    public DateTimeOffset? LastProbeTime
    {
        get => _lastProbeTime;
        set
        {
            if (SetProperty(ref _lastProbeTime, value))
            {
                RaisePropertyChanged(nameof(LastProbeTimeLabel));
                RaisePropertyChanged(nameof(InternetStatusLabel));
                RaisePropertyChanged(nameof(BadgeStatusLabel));
            }
        }
    }

    public string ProbeReasonCode
    {
        get => _probeReasonCode;
        set
        {
            if (SetProperty(ref _probeReasonCode, value))
            {
                RaisePropertyChanged(nameof(InternetStatusLabel));
                RaisePropertyChanged(nameof(BadgeStatusLabel));
            }
        }
    }

    public string InternetStatusLabel => !IsEnabled
        ? "网卡已禁用"
        : !IsConnected
            ? "链路断开"
            : !LastProbeTime.HasValue
                ? "等待检测"
                : IsInternetOnline
                    ? "可访问互联网"
                    : ProbeReasonCode == "PROBE_ROUTE_UNAVAILABLE"
                        ? "仅本地网络"
                        : "联网探测失败";

    public string LastProbeTimeLabel => LastProbeTime.HasValue
        ? LastProbeTime.Value.ToString("HH:mm:ss")
        : "从未检测";

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
