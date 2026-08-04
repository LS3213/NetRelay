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
    private bool _isForceExiting;
    private bool _startupUpdateCheckStarted;
    private bool _tabSelectionIndicatorInitialized;

    public MainWindow(
        ConfigurationService configService,
        NativeNetworkConnectionService connectionService,
        ConnectivityService connectivityService,
        RuleEngine ruleEngine,
        RuleSchedulerService ruleScheduler,
        LogService logService)
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateAdaptiveLayout();

        var connectivity = connectivityService;
        _connectionService = connectionService;

        _ruleEngine = ruleEngine;
        _ruleScheduler = ruleScheduler;

        var logs = logService;
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
            UpdateAdaptiveLayout();
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

        _viewModel.RequestEditRule += OnRequestEditRule;

        SourceInitialized += (_, _) =>
        {
            WindowBackdrop.Apply(this);
        };
        StateChanged += (_, _) => UpdateMaximizeIcon();
        UpdateMaximizeIcon();

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

    private void UpdateAdaptiveLayout()
    {
        var compactHeight = ActualHeight > 0 && ActualHeight < 760;
        var tightHeight = ActualHeight > 0 && ActualHeight < 670;
        var compactWidth = ActualWidth > 0 && ActualWidth < 1080;

        WindowContentHost.Margin = compactHeight
            ? new Thickness(22, 14, 22, 22)
            : new Thickness(28, 18, 28, 30);

        AboutPageRoot.Margin = compactHeight
            ? new Thickness(0, 4, 0, 0)
            : new Thickness(0, 10, 0, 0);
        AboutHeaderGrid.Margin = compactHeight
            ? new Thickness(25, 4, 25, 12)
            : new Thickness(25, 10, 25, 20);
        AboutContentCard.Padding = tightHeight
            ? new Thickness(22, 18, 22, 20)
            : compactHeight
                ? new Thickness(24, 22, 24, 22)
                : new Thickness(30);

        var logoSize = tightHeight ? 58 : compactHeight ? 72 : 90;
        AboutLogoBorder.Width = logoSize;
        AboutLogoBorder.Height = logoSize;
        AboutLogoBorder.CornerRadius = new CornerRadius(tightHeight ? 22 : compactHeight ? 26 : 32);
        AboutLogoIcon.FontSize = tightHeight ? 30 : compactHeight ? 38 : 48;

        AboutBrandPanel.Margin = tightHeight
            ? new Thickness(0, 0, 0, 12)
            : compactHeight
                ? new Thickness(0, 0, 0, 16)
                : new Thickness(0, 0, 0, 26);
        AboutNameText.FontSize = tightHeight ? 24 : compactHeight ? 26 : 30;
        AboutNameText.Margin = tightHeight
            ? new Thickness(0, 10, 0, 0)
            : compactHeight
                ? new Thickness(0, 14, 0, 0)
                : new Thickness(0, 20, 0, 0);
        AboutSubtitleText.FontSize = tightHeight ? 12 : 13;

        AboutStatusGrid.Margin = compactHeight
            ? new Thickness(0, 0, 0, 16)
            : new Thickness(0, 0, 0, 24);
        AboutStatusSpacerColumn.Width = new GridLength(compactWidth ? 12 : 18);
        AboutActionsPanel.Margin = tightHeight
            ? new Thickness(0, 0, 0, 6)
            : new Thickness(0);
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
                CloseHostedWindowToTray(e);
                return;
            }
            else // Exit
            {
                _isForceExiting = true;
                System.Windows.Application.Current.Shutdown();
                return;
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
                    CloseHostedWindowToTray(e);
                    return;
                }
                else
                {
                    _isForceExiting = true;
                    System.Windows.Application.Current.Shutdown();
                    return;
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
}
