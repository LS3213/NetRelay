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
        set => SetProperty(ref _selectedAdapter, value);
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

            SelectedAdapter = Adapters.FirstOrDefault(adapter => adapter.Id == selectedId) ?? Adapters.FirstOrDefault();
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
}
