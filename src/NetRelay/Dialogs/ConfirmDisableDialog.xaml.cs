using System.Windows;
using NetRelay.Models;

namespace NetRelay.Dialogs;

public partial class ConfirmDisableDialog : Window
{
    public ConfirmDisableDialog(NetworkAdapterInfo adapter)
    {
        InitializeComponent();
        AdapterNameText.Text = adapter.Name;
        AdapterDetailsText.Text = $"{adapter.ClassificationLabel} · {adapter.TypeLabel} · {adapter.TrafficRateLabel}";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}

