using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfControl = System.Windows.Controls.Control;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace NetRelay.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly ContextMenu _contextMenu;

    public event EventHandler? OpenRequested;
    public event EventHandler? CheckUpdatesRequested;
    public event EventHandler? ExportDiagnosticsRequested;
    public event EventHandler? OpenLogsRequested;
    public event EventHandler? OpenUpdateCacheRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        _contextMenu = BuildContextMenu();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "NetRelay 原生网络切换助手",
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notifyIcon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowBalloonTip(int timeout, string title, string text, Forms.ToolTipIcon icon)
    {
        _notifyIcon.ShowBalloonTip(timeout, title, text, icon);
    }

    public void Dispose()
    {
        _contextMenu.IsOpen = false;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private void OnNotifyIconMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Right)
        {
            return;
        }

        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            _contextMenu.Placement = PlacementMode.MousePoint;
            _contextMenu.IsOpen = true;
        });
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(WpfColor.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            Background = new SolidColorBrush(WpfColor.FromArgb(0xE8, 0xF4, 0xF8, 0xFD)),
            Padding = new Thickness(2),
            Template = CreateContextMenuTemplate()
        };

        menu.Resources.Add(typeof(MenuItem), CreateMenuItemStyle());
        menu.Resources.Add(typeof(Separator), CreateSeparatorStyle());
        menu.Items.Add(CreateMenuItem("打开主窗口", "\uE8A7", "#536EF2", () => OpenRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateMenuItem("检查更新", "\uE895", "#536EF2", () => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateMenuItem("导出诊断包", "\uE8A7", "#536EF2", () => ExportDiagnosticsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateMenuItem("打开日志目录", "\uE838", "#536EF2", () => OpenLogsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateMenuItem("打开更新缓存", "\uE7C3", "#536EF2", () => OpenUpdateCacheRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("退出程序", "\uE711", "#E85C63", () => ExitRequested?.Invoke(this, EventArgs.Empty)));
        return menu;
    }

    private static MenuItem CreateMenuItem(string header, string icon, string iconColor, Action clickAction)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock
            {
                Text = icon,
                FontFamily = new WpfFontFamily("Segoe Fluent Icons"),
                FontSize = 12,
                Foreground = (WpfBrush)new BrushConverter().ConvertFromString(iconColor)!,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        item.Click += (_, _) => clickAction();
        return item;
    }

    private static ControlTemplate CreateContextMenuTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(WpfControl.BorderThicknessProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(WpfControl.BorderBrushProperty));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(WpfControl.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        border.SetValue(Border.PaddingProperty, new Thickness(4));
        border.SetValue(Border.EffectProperty, new DropShadowEffect
        {
            BlurRadius = 20,
            ShadowDepth = 4,
            Opacity = 0.12,
            Color = WpfColor.FromRgb(0x26, 0x37, 0x53)
        });

        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        presenter.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Cycle);
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(ContextMenu))
        {
            VisualTree = border
        };
    }

    private static Style CreateMenuItemStyle()
    {
        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(WpfControl.FontFamilyProperty, new WpfFontFamily("Segoe UI Variable Text, Microsoft YaHei UI")));
        style.Setters.Add(new Setter(WpfControl.FontSizeProperty, 12.0));
        style.Setters.Add(new Setter(WpfControl.ForegroundProperty, new SolidColorBrush(WpfColor.FromRgb(0x18, 0x27, 0x3D))));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 36.0));
        style.Setters.Add(new Setter(WpfControl.TemplateProperty, CreateMenuItemTemplate()));
        return style;
    }

    private static ControlTemplate CreateMenuItemTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bg";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2));
        border.SetValue(Border.PaddingProperty, new Thickness(10, 0, 10, 0));
        border.SetValue(Border.BackgroundProperty, WpfBrushes.Transparent);

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, WpfOrientation.Horizontal);
        panel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var iconPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        iconPresenter.SetValue(ContentPresenter.ContentSourceProperty, "Icon");
        iconPresenter.SetValue(FrameworkElement.WidthProperty, 24.0);
        iconPresenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, WpfHorizontalAlignment.Left);
        iconPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var headerPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        headerPresenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        headerPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        headerPresenter.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));

        panel.AppendChild(iconPresenter);
        panel.AppendChild(headerPresenter);
        border.AppendChild(panel);

        var template = new ControlTemplate(typeof(MenuItem))
        {
            VisualTree = border
        };

        template.Triggers.Add(CreateMenuHighlightTrigger(UIElement.IsMouseOverProperty));
        template.Triggers.Add(CreateMenuHighlightTrigger(MenuItem.IsHighlightedProperty));
        return template;
    }

    private static Trigger CreateMenuHighlightTrigger(DependencyProperty property)
    {
        var trigger = new Trigger { Property = property, Value = true };
        trigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(WpfColor.FromArgb(0x20, 0, 0, 0)), "Bg"));
        return trigger;
    }

    private static Style CreateSeparatorStyle()
    {
        var style = new Style(typeof(Separator));
        style.Setters.Add(new Setter(WpfControl.BackgroundProperty, new SolidColorBrush(WpfColor.FromArgb(0x15, 0, 0, 0))));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4)));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 1.0));
        return style;
    }

    private static DrawingIcon LoadIcon()
    {
        try
        {
            var resourceUri = new Uri("pack://application:,,,/Assets/NetRelay.ico");
            var streamInfo = WpfApplication.GetResourceStream(resourceUri);
            if (streamInfo != null)
            {
                using var stream = streamInfo.Stream;
                using var icon = new DrawingIcon(stream);
                return (DrawingIcon)icon.Clone();
            }
        }
        catch
        {
        }

        try
        {
            var mainModuleFile = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mainModuleFile))
            {
                return DrawingIcon.ExtractAssociatedIcon(mainModuleFile) ?? DrawingSystemIcons.Application;
            }
        }
        catch
        {
        }

        return DrawingSystemIcons.Application;
    }
}
