using System;
using System.Windows;
using System.Windows.Media;
using NetRelay.Contracts;
using NetRelay.Services;

namespace NetRelay.Dialogs;

public partial class AnnouncementDialog : Window
{
    public AnnouncementDto Announcement { get; }

    public AnnouncementDialog(AnnouncementDto announcement)
    {
        Announcement = announcement;
        InitializeComponent();
        ConfigureUI();
    }

    private void ConfigureUI()
    {
        TitleTextBlock.Text = Announcement.Title;
        SubTitleTextBlock.Text = $"发布时间: {Announcement.PublishedAt.ToLocalTime():yyyy-MM-dd HH:mm}";

        // Configure theme color/icon according to severity
        if (string.Equals(
                Announcement.Severity,
                "critical",
                StringComparison.OrdinalIgnoreCase))
        {
            IconBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0xE8, 0x11, 0x23));
            IconBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xE8, 0x11, 0x23));
            IconTextBlock.Text = "\uE7BA"; // Warning icon
            IconTextBlock.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0x11, 0x23));
            ActionButton.Content = "退出程序";
            
            if (System.Windows.Application.Current.TryFindResource("DangerButtonStyle") is Style dangerStyle)
            {
                ActionButton.Style = dangerStyle;
            }
        }
        else if (string.Equals(
                     Announcement.Severity,
                     "important",
                     StringComparison.OrdinalIgnoreCase))
        {
            IconBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0xF7, 0xA0, 0x20));
            IconBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xF7, 0xA0, 0x20));
            IconTextBlock.Text = "\uE946"; // Info icon
            IconTextBlock.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF7, 0xA0, 0x20));
            ActionButton.Content = "我知道了";
        }
        else
        {
            // Default normal
            IconBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0x53, 0x6E, 0xF2));
            IconBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0x53, 0x6E, 0xF2));
            IconTextBlock.Text = "\uE715"; // Message icon
            IconTextBlock.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x6E, 0xF2));
            ActionButton.Content = "我知道了";
        }

        // Render document securely
        DocViewer.Document = SafeMarkdownParser.Parse(Announcement.Content);
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
