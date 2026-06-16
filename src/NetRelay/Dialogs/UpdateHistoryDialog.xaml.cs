using System.Windows;
using System.Windows.Controls;
using NetRelay.Contracts;

namespace NetRelay.Dialogs;

public partial class UpdateHistoryDialog : Window
{
    public UpdateHistoryDialog(IReadOnlyList<UpdateHistoryItem> items)
    {
        InitializeComponent();
        DataContext = items;
        if (items.Count > 0) HistoryList.SelectedIndex = 0;
        else
        {
            DetailVersionText.Text = "暂无更新历史";
            DetailDateText.Text = "当前通道暂无已发布的版本";
            DetailsText.Text = "后台没有返回可展示的已发布更新记录。";
            PolicyBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is UpdateHistoryItem item)
        {
            DetailVersionText.Text = $"v{item.Version}";
            DetailDateText.Text = $"发布时间：{item.ReleaseDate.ToLocalTime():yyyy-MM-dd HH:mm}";
            PolicyText.Text = item.IsMandatory ? "强制更新" : "可选更新";
            PolicyBadge.Background = new System.Windows.Media.SolidColorBrush(
                item.IsMandatory
                    ? System.Windows.Media.Color.FromArgb(0x14, 0xE8, 0x5C, 0x63)
                    : System.Windows.Media.Color.FromArgb(0x14, 0x53, 0x6E, 0xF2));
            PolicyBadge.BorderBrush = new System.Windows.Media.SolidColorBrush(
                item.IsMandatory
                    ? System.Windows.Media.Color.FromArgb(0x30, 0xE8, 0x5C, 0x63)
                    : System.Windows.Media.Color.FromArgb(0x30, 0x53, 0x6E, 0xF2));
            PolicyText.Foreground = new System.Windows.Media.SolidColorBrush(
                item.IsMandatory
                    ? System.Windows.Media.Color.FromRgb(0xD9, 0x4A, 0x5C)
                    : System.Windows.Media.Color.FromRgb(0x53, 0x6E, 0xF2));
            PolicyBadge.Visibility = Visibility.Visible;
            DetailsText.Text = string.IsNullOrWhiteSpace(item.Changelog) ? "未提供更新日志。" : item.Changelog;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
