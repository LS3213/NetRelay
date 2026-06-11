using System.Collections.ObjectModel;
using NetRelay.Infrastructure;
using NetRelay.Models;
using NetRelay.Services;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;

namespace NetRelay.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly NetworkAdapterService _adapterService;
    private readonly ConfigurationService _configService;
    private readonly ConnectivityService _connectivityService;
    private NetworkAdapterInfo? _selectedAdapter;
    private string? _errorMessage;
    private bool _isLoading;
    private bool _isOperating;
    private string? _operationMessage;
    private readonly DispatcherTimer _trafficTimer;
    private CancellationTokenSource? _probeCts;
    private int _probeTickCount = 0;
    private bool _isProbing;

    public MainViewModel(
        NetworkAdapterService adapterService,
        ConfigurationService configService,
        ConnectivityService connectivityService)
    {
        _adapterService = adapterService;
        _configService = configService;
        _connectivityService = connectivityService;
        RefreshCommand = new RelayCommand(RefreshAdapters, () => !IsLoading);
        RefreshAdapters();
        _trafficTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _trafficTimer.Tick += (_, _) => SampleTraffic();
        _trafficTimer.Start();
    }

    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = [];
    public RelayCommand RefreshCommand { get; }
    public ConfigurationService ConfigService => _configService;

    public NetworkAdapterInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (SetProperty(ref _selectedAdapter, value))
            {
                RaisePropertyChanged(nameof(CanOperateSelectedAdapter));
                TriggerSelectedAdapterProbe(force: true);
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasOperationMessage => !string.IsNullOrWhiteSpace(OperationMessage);
    public bool CanOperateSelectedAdapter => SelectedAdapter?.CanToggle == true && !IsOperating;

    public string? OperationMessage
    {
        get => _operationMessage;
        private set
        {
            if (SetProperty(ref _operationMessage, value))
            {
                RaisePropertyChanged(nameof(HasOperationMessage));
            }
        }
    }

    public bool IsOperating
    {
        get => _isOperating;
        private set
        {
            if (SetProperty(ref _isOperating, value))
            {
                RaisePropertyChanged(nameof(CanOperateSelectedAdapter));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int ConnectedCount => Adapters.Count(adapter => adapter.IsConnected);
    public string NetworkSummary => ConnectedCount > 0 ? "网络已连接" : "当前无连接";
    public string ConnectedDescription => $"{ConnectedCount} 个接口处于连接状态";

    public async Task<AdapterActionResult> SetSelectedAdapterEnabledAsync(
        NativeNetworkConnectionService connectionService,
        bool enabled)
    {
        if (IsOperating)
        {
            return new AdapterActionResult(false, "已有网卡操作正在执行，请稍候。");
        }

        var adapter = SelectedAdapter;
        if (adapter is null)
        {
            return new AdapterActionResult(false, "请先选择目标网卡。");
        }

        if (!adapter.CanToggle)
        {
            return new AdapterActionResult(false, $"“{adapter.Name}”不是可控制的 Windows 网络连接。");
        }

        IsOperating = true;
        OperationMessage = enabled ? $"正在启用“{adapter.Name}”…" : $"正在禁用“{adapter.Name}”…";
        try
        {
            var result = await Task.Run(() => connectionService.SetEnabled(adapter.Id, adapter.Name, enabled));
            OperationMessage = result.Message;
            await Task.Delay(700);
            RefreshAdapters();
            return result;
        }
        finally
        {
            IsOperating = false;
        }
    }

    private void RefreshAdapters()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var selectedId = SelectedAdapter?.Id;
            var newAdapters = _adapterService.GetAdapters();

            // 1. 移除已不存在的网卡
            var newIds = newAdapters.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int i = Adapters.Count - 1; i >= 0; i--)
            {
                if (!newIds.Contains(Adapters[i].Id))
                {
                    Adapters.RemoveAt(i);
                }
            }

            // 2. 新增或更新网卡状态（保留实例以维系历史流量数据）
            foreach (var newAdapter in newAdapters)
            {
                var existing = Adapters.FirstOrDefault(a => string.Equals(a.Id, newAdapter.Id, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    Adapters.Add(newAdapter);
                }
                else
                {
                    existing.IsEnabled = newAdapter.IsEnabled;
                    existing.OperationalStatus = newAdapter.OperationalStatus;
                    existing.Speed = newAdapter.Speed;
                    existing.MacAddress = newAdapter.MacAddress;
                    existing.IpAddresses = newAdapter.IpAddresses;
                    existing.ClassificationLabel = newAdapter.ClassificationLabel;
                    existing.CanToggle = newAdapter.CanToggle;
                }
            }

            SelectedAdapter = Adapters.FirstOrDefault(adapter => AdapterIdsEqual(adapter.Id, selectedId))
                ?? Adapters.FirstOrDefault();
            _adapterService.UpdateTraffic(Adapters);
            RaisePropertyChanged(nameof(ConnectedCount));
            RaisePropertyChanged(nameof(NetworkSummary));
            RaisePropertyChanged(nameof(ConnectedDescription));

            SortAdapters();
            TriggerSelectedAdapterProbe(force: true);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"读取网卡失败：{exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SampleTraffic()
    {
        try
        {
            _adapterService.UpdateTraffic(Adapters);
            SortAdapters(); // 流量状态改变可能触发排序变化

            _probeTickCount++;
            if (_probeTickCount >= 5)
            {
                _probeTickCount = 0;
                TriggerSelectedAdapterProbe(force: false);
            }
        }
        catch
        {
            // Traffic visualization is diagnostic only and must not interrupt adapter management.
        }
    }

    private async void TriggerSelectedAdapterProbe(bool force = false)
    {
        if (_isProbing && !force)
        {
            return;
        }

        _probeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        _isProbing = true;

        var adapter = SelectedAdapter;
        if (adapter is null || !adapter.IsEnabled)
        {
            _isProbing = false;
            return;
        }

        try
        {
            await Task.Delay(250, cts.Token);

            var result = await _connectivityService.ProbeAdapterAsync(adapter.Id, _configService.Current.ProbePolicy);

            if (!cts.IsCancellationRequested && SelectedAdapter == adapter)
            {
                adapter.IsInternetOnline = result.Online;
                adapter.LastProbeTime = result.CheckedAt;
                adapter.ProbeReasonCode = result.ReasonCode;
                SortAdapters(); // 联网状态改变可能触发排序变化
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = $"探测发生异常：{exception.Message}\n{exception.StackTrace}";
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                _isProbing = false;
            }
        }
    }

    private void SortAdapters()
    {
        var sorted = Adapters
            .OrderBy(GetSortOrder)
            .ThenBy(a => a.IsLikelyVirtual)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            var currentIndex = Adapters.IndexOf(sorted[i]);
            if (currentIndex != i)
            {
                Adapters.Move(currentIndex, i);
            }
        }
    }

    private static int GetSortOrder(NetworkAdapterInfo adapter)
    {
        // 1. 已连接 (IsConnected == true) 状态组
        if (adapter.IsConnected)
        {
            // 1.1 联网在线 -> 优先级最顶层 (0)
            if (adapter.IsInternetOnline) return 0;

            // 1.2 有流量活动 -> (1)
            if (adapter.HasTraffic) return 1;

            // 1.3 无流量活动 -> (2)
            return 2;
        }

        // 2. 未连接 (IsEnabled == true && IsConnected == false) 状态组
        if (adapter.IsEnabled)
        {
            return 3;
        }

        // 3. 已禁用 (IsEnabled == false) 状态组
        // 3.1 禁用但最近有流量活动 -> (4)
        bool hasRecentTraffic = adapter.TrafficHistory.Any(t => t > 0);
        if (hasRecentTraffic) return 4;

        // 3.2 禁用且无流量活动 -> 最底端 (5)
        return 5;
    }

    private static bool AdapterIdsEqual(string adapterId, string? selectedId)
    {
        if (selectedId is null)
        {
            return false;
        }

        return Guid.TryParse(adapterId, out var adapterGuid) && Guid.TryParse(selectedId, out var selectedGuid)
            ? adapterGuid == selectedGuid
            : string.Equals(adapterId, selectedId, StringComparison.OrdinalIgnoreCase);
    }
}
