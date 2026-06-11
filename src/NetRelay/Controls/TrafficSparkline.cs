using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using NetRelay.Models;

namespace NetRelay.Controls;

public sealed class TrafficSparkline : FrameworkElement
{
    public static readonly DependencyProperty AdapterProperty = DependencyProperty.Register(
        nameof(Adapter),
        typeof(NetworkAdapterInfo),
        typeof(TrafficSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, AdapterChanged));

    public NetworkAdapterInfo? Adapter
    {
        get => (NetworkAdapterInfo?)GetValue(AdapterProperty);
        set => SetValue(AdapterProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var baselinePen = new Pen(new SolidColorBrush(Color.FromArgb(55, 91, 108, 134)), 1);
        drawingContext.DrawLine(baselinePen, new Point(0, ActualHeight / 2), new Point(ActualWidth, ActualHeight / 2));

        var adapter = Adapter;
        var samples = adapter?.TrafficHistory;
        if (adapter is null || samples is null || samples.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var peak = Math.Max(samples.Max(), 1024);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var index = 0; index < samples.Count; index++)
            {
                var x = index * ActualWidth / Math.Max(samples.Count - 1, 1);
                var normalized = Math.Log10(samples[index] + 1) / Math.Log10(peak + 1);
                var y = ActualHeight - 2 - normalized * (ActualHeight - 4);
                var point = new Point(x, y);

                if (index == 0)
                {
                    context.BeginFigure(point, false, false);
                }
                else
                {
                    context.LineTo(point, true, false);
                }
            }
        }
        geometry.Freeze();

        var lineBrush = new SolidColorBrush(adapter.HasTraffic
            ? Color.FromRgb(63, 111, 225)
            : Color.FromRgb(126, 141, 163));
        var linePen = new Pen(lineBrush, 1.35)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        drawingContext.DrawGeometry(null, linePen, geometry);
    }

    private static void AdapterChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        var sparkline = (TrafficSparkline)dependencyObject;
        if (eventArgs.OldValue is NetworkAdapterInfo oldAdapter)
        {
            oldAdapter.PropertyChanged -= sparkline.AdapterPropertyChanged;
        }
        if (eventArgs.NewValue is NetworkAdapterInfo newAdapter)
        {
            newAdapter.PropertyChanged += sparkline.AdapterPropertyChanged;
        }
        sparkline.InvalidateVisual();
    }

    private void AdapterPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(NetworkAdapterInfo.TrafficHistory) or nameof(NetworkAdapterInfo.HasTraffic))
        {
            InvalidateVisual();
        }
    }
}
