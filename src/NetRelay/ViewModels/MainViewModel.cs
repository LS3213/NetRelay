using System.Collections.ObjectModel;
using NetRelay.Infrastructure;
using NetRelay.Models;
using NetRelay.Services;
using System.Windows.Threading;

namespace NetRelay.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly NetworkAdapterService _adapterService;
    private NetworkAdapterInfo? _selectedAdapter;
    private string? _errorMessage;
    private bool _isLoading;
    private bool _isOperating;
    private string? _operationMessage;
    private readonly DispatcherTimer _trafficTimer;

    public MainViewModel(NetworkAdapterService adapterService)
    {
        _adapterService = adapterService;
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

    public NetworkAdapterInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (SetProperty(ref _selectedAdapter, value))
            {
                RaisePropertyChanged(nameof(CanOperateSelectedAdapter));
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
            var adapters = _adapterService.GetAdapters();
            Adapters.Clear();
            foreach (var adapter in adapters)
            {
                Adapters.Add(adapter);
            }

            SelectedAdapter = Adapters.FirstOrDefault(adapter => AdapterIdsEqual(adapter.Id, selectedId))
                ?? Adapters.FirstOrDefault();
            _adapterService.UpdateTraffic(Adapters);
            RaisePropertyChanged(nameof(ConnectedCount));
            RaisePropertyChanged(nameof(NetworkSummary));
            RaisePropertyChanged(nameof(ConnectedDescription));
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
        }
        catch
        {
            // Traffic visualization is diagnostic only and must not interrupt adapter management.
        }
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
