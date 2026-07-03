using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NetRelay.Dialogs;
using NetRelay.Native;
using NetRelay.Services;
using NetRelay.ViewModels;
using NetRelay.Models;
using System.Linq;
using System;

namespace NetRelay;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly NativeNetworkConnectionService _connectionService;
    private readonly RuleEngine _ruleEngine;
    private readonly RuleSchedulerService _ruleScheduler;
    private readonly SettingsRuntimeService _settingsRuntimeService;
    private readonly bool _ownsRuntime;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private bool _isForceExiting;
    private readonly bool _startMinimized;
    private bool _startupUpdateCheckStarted;
    private bool _tabSelectionIndicatorInitialized;

    public MainWindow() : this(new ConfigurationService())
    {
    }

    public MainWindow(
        ConfigurationService configService,
        NativeNetworkConnectionService? connectionService = null,
        ConnectivityService? connectivityService = null,
        RuleEngine? ruleEngine = null,
        RuleSchedulerService? ruleScheduler = null,
        LogService? logService = null,
        bool ownsRuntime = true)
    {
        _ownsRuntime = ownsRuntime;
        var commandLineArgs = Environment.GetCommandLineArgs();
        _startMinimized = commandLineArgs.Contains("--protocol-launch", StringComparer.OrdinalIgnoreCase);
        if (_startMinimized)
        {
            Opacity = 0;
            ShowInTaskbar = false;
        }

        InitializeComponent();
        if (_ownsRuntime)
        {
            RichToastService.Initialize();
        }

        var connectivity = connectivityService ?? new ConnectivityService();
        _connectionService = connectionService ?? new NativeNetworkConnectionService();

        _ruleEngine = ruleEngine ?? new RuleEngine(_connectionService, connectivity, configService);
        _ruleScheduler = ruleScheduler ?? new RuleSchedulerService(_ruleEngine, configService, connectivity);
        if (ruleScheduler is null)
        {
            _ruleScheduler.Start();
        }

        var logs = logService ?? new LogService();
        _settingsRuntimeService = new SettingsRuntimeService(configService, logs, () => _ruleScheduler.Reload());

        _viewModel = new MainViewModel(
            new NetworkAdapterService(_connectionService),
            configService,
            connectivity,
            _ruleEngine,
            _ruleScheduler,
            logs);
        DataContext = _viewModel;
        Loaded += async (_, _) =>
        {
            _viewModel.ResumeUiMonitoring();
            await CheckForUpdatesOnStartupOnceAsync();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _viewModel.ResumeUiMonitoring();
            }
            else
            {
                _viewModel.PauseUiMonitoring();
            }
        };

        // Listen to events
        if (_ownsRuntime)
        {
            _ruleScheduler.PreNotificationTriggered += OnSchedulerPreNotificationTriggered;
            _ruleEngine.ExecutionRecorded += OnRuleExecutionRecorded;
        }
        _viewModel.RequestEditRule += OnRequestEditRule;

        SourceInitialized += (_, _) =>
        {
            WindowBackdrop.Apply(this);
            if (_startMinimized)
            {
                Hide();
                Opacity = 1;
            }
        };
        StateChanged += (_, _) => UpdateMaximizeIcon();
        UpdateMaximizeIcon();

        if (_ownsRuntime)
        {
            InitializeNotifyIcon();
            if (!_viewModel.ConfigService.IsAutomationEnabled)
            {
                _notifyIcon?.ShowBalloonTip(
                    8000,
                    "NetRelay 自动化已暂停",
                    _viewModel.ConfigService.AutomationDisabledReason ?? "探测配置无效，请检查配置文件。",
                    System.Windows.Forms.ToolTipIcon.Warning);
            }
        }
        UpdateTabSelection(0);
    }

    public bool IsForceExiting => _isForceExiting;

    public async Task CheckForUpdatesOnStartupOnceAsync()
    {
        if (_startupUpdateCheckStarted)
        {
            return;
        }

        _startupUpdateCheckStarted = true;
        await _viewModel.CheckForUpdatesOnStartupAsync();
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(
            _viewModel.ConfigService.Current,
            _viewModel.UpdateStatusSnapshot,
            _viewModel.ConfigService.IsAutomationEnabled,
            _viewModel.ConfigService.AutomationDisabledReason,
            _viewModel.ExportDiagnosticsAsync,
            _viewModel.OpenLogsDirectory,
            _viewModel.OpenUpdateCacheDirectory,
            _viewModel.ClearUpdateCacheAsync)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            var result = await _settingsRuntimeService.ApplyAsync();
            if (result.Success)
            {
                await _viewModel.LoadLogsAsync();
            }
            _viewModel.NotifySettingsChanged();

            ModernMessageBox.Show(
                this,
                result.Message,
                result.Success ? "NetRelay 设置" : "NetRelay 设置校验失败",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void EnableAdapterButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetSelectedAdapterEnabledAsync(_connectionService, true);
    }

    private async void DisableAdapterButton_Click(object sender, RoutedEventArgs e)
    {
        var adapter = _viewModel.SelectedAdapter;
        if (adapter is null)
        {
            return;
        }

        var dialog = new ConfirmDisableDialog(adapter)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.SetSelectedAdapterEnabledAsync(_connectionService, false);
        }
    }

    private void UpdateMaximizeIcon()
    {
        MaximizeIcon.Data = Geometry.Parse(
            WindowState == WindowState.Maximized
                ? "M 3,1 L 10,1 L 10,8 M 1,3 L 8,3 L 8,10 L 1,10 Z"
                : "M 1,1 L 10,1 L 10,10 L 1,10 Z");
    }

    private void InitializeNotifyIcon()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon();

        try
        {
            var resourceUri = new Uri("pack://application:,,,/Assets/NetRelay.ico");
            var streamInfo = System.Windows.Application.GetResourceStream(resourceUri);
            if (streamInfo != null)
            {
                using (var stream = streamInfo.Stream)
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(stream);
                }
            }
        }
        catch
        {
            try
            {
                var mainModuleFile = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(mainModuleFile))
                {
                    _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(mainModuleFile);
                }
                else
                {
                    var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                }
            }
            catch
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
        }

        _notifyIcon.Text = "NetRelay 原生网络切换助手";
        _notifyIcon.Visible = true;

        _notifyIcon.DoubleClick += (s, e) => RestoreWindow();
        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        _notifyIcon.BalloonTipClicked += (s, e) => RestoreWindow();
    }

    private void NotifyIcon_MouseClick(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == System.Windows.Forms.MouseButtons.Right)
        {
            var menu = (System.Windows.Controls.ContextMenu)FindResource("TrayContextMenu");
            if (menu != null)
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(hwnd);
                }

                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                menu.IsOpen = true;
            }
        }
    }

    private void TrayOpen_Click(object sender, RoutedEventArgs e)
    {
        RestoreWindow();
    }

    private void TrayCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        RestoreWindow();
        _viewModel.CurrentTabIndex = 3;
        UpdateTabSelection(3);
        _viewModel.CheckUpdatesCommand.Execute(null);
    }

    private async void TrayExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        RestoreWindow();
        _viewModel.CurrentTabIndex = 2;
        UpdateTabSelection(2);
        await _viewModel.ExportDiagnosticsAsync();
    }

    private void TrayOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenLogsDirectory();
    }

    private void TrayOpenUpdateCache_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenUpdateCacheDirectory();
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    public void SelectTab(int tabIndex)
    {
        _viewModel.CurrentTabIndex = tabIndex;
        UpdateTabSelection(tabIndex);
        if (tabIndex == 2)
        {
            _ = _viewModel.LoadLogsAsync();
        }
    }

    public void CheckUpdatesInteractive()
    {
        _viewModel.CheckUpdatesCommand.Execute(null);
    }

    public void ClearPendingNotificationIfMatches(Guid notificationActionId)
    {
        _viewModel.ClearPendingNotificationIfMatches(notificationActionId);
    }

    public void ForceCloseFromRuntime()
    {
        _isForceExiting = true;
        _viewModel.Shutdown();
        DataContext = null;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        Close();
    }

    public void RestoreWindow()
    {
        ShowInTaskbar = true;
        Show();
        _viewModel.ResumeUiMonitoring();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void ExitApplication()
    {
        if (!_ownsRuntime)
        {
            _isForceExiting = true;
            _viewModel.Shutdown();
            DataContext = null;
            System.Windows.Application.Current.Shutdown();
            return;
        }

        Hide();
        _viewModel.Shutdown();
        _ruleScheduler?.Stop();
        _ruleScheduler?.Dispose();
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _isForceExiting = true;
        Close();
    }

    private void OnSchedulerPreNotificationTriggered(object? sender, PreNotificationEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // Use one native interactive toast. Sending a legacy tray balloon as
            // well would produce duplicate notifications for the same event.
            var actionName = e.Rule.Action == RuleAction.Enable ? "启用" : "禁用";
            RichToastService.ShowPreNotification(e, actionName);
        });
    }

    private void OnRuleExecutionRecorded(object? sender, ExecutionRecord record)
    {
        if (record.Source == RuleSource.Manual)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            var title = record.Outcome switch
            {
                "SUCCESS" when record.Source == RuleSource.Recovery => "NetRelay 自动恢复完成",
                "SUCCESS" => "NetRelay 自动操作完成",
                "SKIPPED" => "NetRelay 自动操作已跳过",
                _ => "NetRelay 自动操作失败"
            };
            var message = record.ReasonCode switch
            {
                "OK" => "网卡操作已成功完成。",
                "BACKUP_NETWORK_UNAVAILABLE" => "未找到可联网的备用网卡，已取消禁用操作。",
                "POST_SWITCH_VALIDATION_FAILED" => "切换后备用网络失效，已执行安全回滚。",
                "ROLLBACK_SUCCEEDED" => "目标网卡已重新启用。",
                "ROLLBACK_FAILED" => "安全回滚失败，请手动重新启用目标网卡。",
                "RECOVERY_ALREADY_ENABLED" => "目标网卡已经启用，无需重复恢复。",
                "CONFIG_INVALID" => "探测配置无效，自动化规则已暂停。",
                "CONDITION_NOT_MET" => "规则附加条件不满足，本次操作已跳过。",
                _ => $"执行结果：{record.ReasonCode}"
            };

            _notifyIcon?.ShowBalloonTip(6000, title, message, GetNotificationIcon(record.Outcome));
        });
    }

    private static System.Windows.Forms.ToolTipIcon GetNotificationIcon(string outcome)
    {
        return outcome switch
        {
            "SUCCESS" => System.Windows.Forms.ToolTipIcon.Info,
            "SKIPPED" => System.Windows.Forms.ToolTipIcon.Warning,
            _ => System.Windows.Forms.ToolTipIcon.Error
        };
    }

    private void OnRequestEditRule(AutomationRule? rule)
    {
        var dialog = new RuleEditDialog(rule, _viewModel.Adapters.ToList(), _viewModel.ConfigService.Current)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.ResultRule != null)
        {
            var result = dialog.ResultRule;
            var currentRules = _viewModel.ConfigService.Current.Rules;

            if (rule == null)
            {
                currentRules.Add(result);
            }
            else
            {
                var index = currentRules.FindIndex(r => r.Id == rule.Id);
                if (index >= 0)
                {
                    currentRules[index] = result;
                }
            }

            _viewModel.ConfigService.Save();
            _viewModel.LoadRules();
            if (rule is null)
            {
                _viewModel.ReloadRules();
            }
            else
            {
                _viewModel.ReloadEditedRule(rule.Id);
            }
        }
    }

    private void TabOverviewButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CurrentTabIndex = 0;
        UpdateTabSelection(0);
    }

    private void TabRulesButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CurrentTabIndex = 1;
        UpdateTabSelection(1);
    }

    private void TabLogsButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CurrentTabIndex = 2;
        UpdateTabSelection(2);
        _ = _viewModel.LoadLogsAsync();
    }

    private void TabAboutButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CurrentTabIndex = 3;
        UpdateTabSelection(3);
    }

    private void UpdateTabSelection(int tabIndex)
    {
        // Specular glass background gradient (refined high-contrast frosted glass)
        var activeBrush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0, 1)
        };
        // Top specular highlight reflection (white gloss, 96% opacity)
        activeBrush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(245, 0xFF, 0xFF, 0xFF), 0.0));
        // Soft water-blue reflection without becoming a solid color block
        activeBrush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(150, 0xEA, 0xF4, 0xFF), 0.42));
        // Translucent glass bottom
        activeBrush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(118, 0xFF, 0xFF, 0xFF), 1.0));

        // Edge refraction gradient border (simulates light reflection on glass edge)
        var activeBorder = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0, 1)
        };
        activeBorder.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(250, 0xFF, 0xFF, 0xFF), 0.0));
        activeBorder.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(145, 0xC7, 0xD5, 0xEE), 1.0));

        var activeText = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x43, 0x5E, 0xEE));

        // Ultra-soft, lightweight dark shadow to prevent muddy glass center
        var activeShadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 18,
            ShadowDepth = 3,
            Opacity = 0.10,
            Color = System.Windows.Media.Color.FromRgb(0x18, 0x22, 0x36)
        };

        var inactiveBrush = System.Windows.Media.Brushes.Transparent;
        var inactiveBorder = System.Windows.Media.Brushes.Transparent;
        var inactiveText = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");

        // Keep BorderThickness constant at 1.0 to prevent 1px text layout shift/shaking on selection
        var uniformBorderThickness = new Thickness(1);

        SetTabButtonState(TabOverviewBtn, tabIndex == 0, inactiveBrush, inactiveBorder, activeText, inactiveText, uniformBorderThickness);
        SetTabButtonState(TabRulesBtn, tabIndex == 1, inactiveBrush, inactiveBorder, activeText, inactiveText, uniformBorderThickness);
        SetTabButtonState(TabLogsBtn, tabIndex == 2, inactiveBrush, inactiveBorder, activeText, inactiveText, uniformBorderThickness);
        SetTabButtonState(TabAboutBtn, tabIndex == 3, inactiveBrush, inactiveBorder, activeText, inactiveText, uniformBorderThickness);

        TabSelectionIndicator.Background = activeBrush;
        TabSelectionIndicator.BorderBrush = activeBorder;
        TabSelectionIndicator.Effect = activeShadow;

        var targetButton = GetTabButton(tabIndex);
        if (targetButton.ActualWidth <= 0 || TabNavigationHost.ActualWidth <= 0)
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateTabSelection(tabIndex)), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        var targetX = targetButton.TranslatePoint(new System.Windows.Point(0, 0), TabNavigationHost).X;
        MoveTabSelectionIndicator(targetX, targetButton.ActualWidth);
    }

    private static void SetTabButtonState(
        System.Windows.Controls.Button button,
        bool isActive,
        System.Windows.Media.Brush inactiveBrush,
        System.Windows.Media.Brush inactiveBorder,
        System.Windows.Media.Brush activeText,
        System.Windows.Media.Brush inactiveText,
        Thickness uniformBorderThickness)
    {
        button.Background = inactiveBrush;
        button.BorderBrush = inactiveBorder;
        button.BorderThickness = uniformBorderThickness;
        button.Foreground = isActive ? activeText : inactiveText;
        button.Effect = null;
    }

    private System.Windows.Controls.Button GetTabButton(int tabIndex)
    {
        return tabIndex switch
        {
            0 => TabOverviewBtn,
            1 => TabRulesBtn,
            2 => TabLogsBtn,
            3 => TabAboutBtn,
            _ => TabOverviewBtn
        };
    }

    private void MoveTabSelectionIndicator(double targetX, double targetWidth)
    {
        if (!_tabSelectionIndicatorInitialized)
        {
            TabSelectionIndicator.Width = targetWidth;
            TabSelectionTransform.X = targetX;
            TabSelectionIndicator.Opacity = 1;
            _tabSelectionIndicatorInitialized = true;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(210);

        TabSelectionTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(targetX, duration) { EasingFunction = ease });
        TabSelectionIndicator.BeginAnimation(
            WidthProperty,
            new DoubleAnimation(targetWidth, duration) { EasingFunction = ease });
        TabSelectionIndicator.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease });
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isForceExiting)
        {
            base.OnClosing(e);
            return;
        }

        var config = _viewModel.ConfigService.Current;

        if (config.DoNotRemindClose)
        {
            if (config.CloseAction == "HideToTray")
            {
                if (!_ownsRuntime)
                {
                    CloseHostedWindowToTray(e);
                    return;
                }

                e.Cancel = true;
                _viewModel.PauseUiMonitoring();
                Hide();
            }
            else // Exit
            {
                if (!_ownsRuntime)
                {
                    _isForceExiting = true;
                    System.Windows.Application.Current.Shutdown();
                    return;
                }

                Hide();
                _viewModel.Shutdown();
                _ruleScheduler?.Stop();
                _ruleScheduler?.Dispose();
                _notifyIcon?.Dispose();
                base.OnClosing(e);
            }
        }
        else
        {
            e.Cancel = true;

            var dialog = new ConfirmCloseDialog()
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                config.CloseAction = dialog.CloseActionResult;
                config.DoNotRemindClose = dialog.DoNotRemindMe;

                _viewModel.ConfigService.Save();

                if (dialog.CloseActionResult == "HideToTray")
                {
                    if (!_ownsRuntime)
                    {
                        CloseHostedWindowToTray(e);
                        return;
                    }

                    _viewModel.PauseUiMonitoring();
                    Hide();
                }
                else
                {
                    if (!_ownsRuntime)
                    {
                        _isForceExiting = true;
                        System.Windows.Application.Current.Shutdown();
                        return;
                    }

                    Hide();
                    _viewModel.Shutdown();
                    _ruleScheduler?.Stop();
                    _ruleScheduler?.Dispose();
                    _notifyIcon?.Dispose();
                    _isForceExiting = true;
                    Close();
                }
            }
        }
    }

    private void CloseHostedWindowToTray(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = false;
        _isForceExiting = true;
        _viewModel.Shutdown();
        DataContext = null;
        ShowInTaskbar = false;
    }

    public void HandleCommandLineArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--protocol-launch", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                HandleProtocolAction(args[i + 1]);
                return;
            }
        }

        if (args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        RestoreWindow();
    }

    public void HandleProtocolAction(string rawUrl)
    {
        try
        {
            if (!NotificationProtocolActivation.TryParse(rawUrl, out var activation) || activation is null)
            {
                return;
            }

            var result = _ruleScheduler.TryApplyPreNotificationAction(
                activation.NotificationId,
                activation.Token,
                activation.Action);
            if (!result.Applied)
            {
                return;
            }

            _viewModel.ClearPendingNotificationIfMatches(activation.NotificationId);
        }
        catch
        {
            // Fail gracefully
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
