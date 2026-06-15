using System;
using System.Linq;
using System.Windows;

namespace NetRelay.Dialogs;

public partial class ModernMessageBox : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;

    public ModernMessageBox()
    {
        InitializeComponent();
    }

    public static MessageBoxResult Show(Window owner, string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
    {
        var box = new ModernMessageBox
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        box.Configure(message, title, buttons, icon);
        box.ShowDialog();
        return box._result;
    }

    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
    {
        var box = new ModernMessageBox();
        var activeWindow = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? System.Windows.Application.Current.MainWindow;
        if (activeWindow != null && activeWindow.IsVisible)
        {
            box.Owner = activeWindow;
            box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            box.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        box.Configure(message, title, buttons, icon);
        box.ShowDialog();
        return box._result;
    }

    private void Configure(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;

        // Configure Icon
        switch (icon)
        {
            case MessageBoxImage.Information:
                IconBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0x53, 0x6E, 0xF2));
                IconBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0x53, 0x6E, 0xF2));
                IconTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x6E, 0xF2));
                IconTextBlock.Text = "\uE946"; // Info
                break;
            case MessageBoxImage.Warning:
                IconBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0xFF, 0x8C, 0x00));
                IconBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0x8C, 0x00));
                IconTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x8C, 0x00));
                IconTextBlock.Text = "\uE7BA"; // Warning
                break;
            case MessageBoxImage.Error:
                IconBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0xFF, 0x4D, 0x4D));
                IconBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0x4D, 0x4D));
                IconTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x4D, 0x4D));
                IconTextBlock.Text = "\uE711"; // Error
                break;
            case MessageBoxImage.Question:
                IconBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0x8A, 0x2B, 0xE2));
                IconBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0x8A, 0x2B, 0xE2));
                IconTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x2B, 0xE2));
                IconTextBlock.Text = "\uE897"; // Question
                break;
            default:
                IconBorder.Visibility = Visibility.Collapsed;
                break;
        }

        // Configure Buttons
        switch (buttons)
        {
            case MessageBoxButton.OK:
                OkButton.Visibility = Visibility.Visible;
                break;
            case MessageBoxButton.OKCancel:
                OkButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                break;
            case MessageBoxButton.YesNo:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                break;
            case MessageBoxButton.YesNoCancel:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                break;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        _result = MessageBoxResult.OK;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _result = MessageBoxResult.Cancel;
        Close();
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        _result = MessageBoxResult.Yes;
        Close();
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        _result = MessageBoxResult.No;
        Close();
    }
}
