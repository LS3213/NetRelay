using System.Windows;
using System.Windows.Media;
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
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private bool _isForceExiting;
    private readonly bool _startMinimized;

    public MainWindow()
    {
        _startMinimized = Environment.GetCommandLineArgs().Contains("--startup", StringComparer.OrdinalIgnoreCase);
        if (_startMinimized)
        {
            Opacity = 0;
            ShowInTaskbar = false;
        }

        InitializeComponent();
        var configService = new ConfigurationService();
        var connectivityService = new ConnectivityService();
        _connectionService = new NativeNetworkConnectionService();
        
        _ruleEngine = new RuleEngine(_connectionService, connectivityService, configService);
        _ruleScheduler = new RuleSchedulerService(_ruleEngine, configService);
        _ruleScheduler.Start();

        _viewModel = new MainViewModel(
            new NetworkAdapterService(_connectionService),
            configService,
            connectivityService,
            _ruleScheduler);
        DataContext = _viewModel;

        // Listen to events
        _ruleScheduler.PreNotificationTriggered += OnSchedulerPreNotificationTriggered;
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

        InitializeNotifyIcon();
        UpdateTabSelection(0);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_viewModel.ConfigService.Current)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.ConfigService.Save();
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

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    private void RestoreWindow()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void ExitApplication()
    {
        Hide();
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
            // System-level notification bubble that auto-dismisses and is non-intrusive for gaming/fullscreen
            _notifyIcon?.ShowBalloonTip(
                5000,
                "NetRelay 计划切换提醒",
                $"规则“{e.Rule.Name}”将在 {e.MinutesRemaining} 分钟后执行，点击处理。",
                System.Windows.Forms.ToolTipIcon.Info
            );
        });
    }

    private void OnRequestEditRule(AutomationRule? rule)
    {
        var dialog = new RuleEditDialog(rule, _viewModel.Adapters.ToList())
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
            _viewModel.ReloadRules();
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

    private void UpdateTabSelection(int tabIndex)
    {
        // Specular glass background gradient (refined high-contrast frosted glass)
        var activeBrush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0, 1)
        };
        // Top specular highlight reflection (white gloss, 92% opacity)
        activeBrush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(235, 0xFF, 0xFF, 0xFF), 0.0));
        // Soft water blue reflection (72% opacity, gives it a distinct but clean color)
        activeBrush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(185, 0xE2, 0xEE, 0xFF), 0.35));
        // Transition white (50% opacity)
        activeBrush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(128, 0xFF, 0xFF, 0xFF), 0.65));
        // Translucent bottom (30% opacity white)
        activeBrush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(76, 0xFF, 0xFF, 0xFF), 1.0));

        // Edge refraction gradient border (simulates light reflection on glass edge)
        var activeBorder = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0, 1)
        };
        // Bright top highlight (96% opacity white)
        activeBorder.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(245, 0xFF, 0xFF, 0xFF), 0.0));
        // Soft middle transition (60% opacity white)
        activeBorder.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(150, 0xFF, 0xFF, 0xFF), 0.5));
        // Soft bottom edge (40% opacity white)
        activeBorder.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(100, 0xFF, 0xFF, 0xFF), 1.0));

        var activeText = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x43, 0x5E, 0xEE));

        // Ultra-soft, lightweight dark shadow to prevent muddy glass center
        var activeShadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 10,
            ShadowDepth = 1.2,
            Opacity = 0.08,
            Color = System.Windows.Media.Color.FromRgb(0x18, 0x22, 0x36)
        };

        var inactiveBrush = System.Windows.Media.Brushes.Transparent;
        var inactiveBorder = System.Windows.Media.Brushes.Transparent;
        var inactiveText = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");

        // Keep BorderThickness constant at 1.0 to prevent 1px text layout shift/shaking on selection
        var uniformBorderThickness = new Thickness(1);

        if (TabOverviewBtn != null)
        {
            TabOverviewBtn.Background = tabIndex == 0 ? activeBrush : inactiveBrush;
            TabOverviewBtn.BorderBrush = tabIndex == 0 ? activeBorder : inactiveBorder;
            TabOverviewBtn.BorderThickness = uniformBorderThickness;
            TabOverviewBtn.Foreground = tabIndex == 0 ? activeText : inactiveText;
            TabOverviewBtn.Effect = tabIndex == 0 ? activeShadow : null;
        }

        if (TabRulesBtn != null)
        {
            TabRulesBtn.Background = tabIndex == 1 ? activeBrush : inactiveBrush;
            TabRulesBtn.BorderBrush = tabIndex == 1 ? activeBorder : inactiveBorder;
            TabRulesBtn.BorderThickness = uniformBorderThickness;
            TabRulesBtn.Foreground = tabIndex == 1 ? activeText : inactiveText;
            TabRulesBtn.Effect = tabIndex == 1 ? activeShadow : null;
        }

        if (TabLogsBtn != null)
        {
            TabLogsBtn.Background = tabIndex == 2 ? activeBrush : inactiveBrush;
            TabLogsBtn.BorderBrush = tabIndex == 2 ? activeBorder : inactiveBorder;
            TabLogsBtn.BorderThickness = uniformBorderThickness;
            TabLogsBtn.Foreground = tabIndex == 2 ? activeText : inactiveText;
            TabLogsBtn.Effect = tabIndex == 2 ? activeShadow : null;
        }
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
                e.Cancel = true;
                Hide();
            }
            else // Exit
            {
                Hide();
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
                    Hide();
                }
                else
                {
                    Hide();
                    _ruleScheduler?.Stop();
                    _ruleScheduler?.Dispose();
                    _notifyIcon?.Dispose();
                    _isForceExiting = true;
                    Close();
                }
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
