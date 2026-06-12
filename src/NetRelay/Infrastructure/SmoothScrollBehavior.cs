using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace NetRelay.Infrastructure;

public static class SmoothScrollBehavior
{
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();
    private static bool _isEnabled;

    public static void Enable()
    {
        if (_isEnabled)
        {
            return;
        }

        _isEnabled = true;
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer
            || FindClosestScrollViewer(e.OriginalSource as DependencyObject) != scrollViewer
            || scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var state = States.GetValue(scrollViewer, viewer => new ScrollState(viewer));
        if (!state.TryScroll(e.Delta))
        {
            return;
        }

        e.Handled = true;
    }

    private static ScrollViewer? FindClosestScrollViewer(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            source = source is Visual or Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    private sealed class ScrollState
    {
        private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(180);
        private readonly ScrollViewer _scrollViewer;
        private readonly DispatcherTimer _timer;
        private double _startOffset;
        private double _targetOffset;
        private DateTime _startedAt;

        public ScrollState(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;
            _targetOffset = scrollViewer.VerticalOffset;
            _timer = new DispatcherTimer(DispatcherPriority.Render, scrollViewer.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _timer.Tick += OnTick;
        }

        public bool TryScroll(int wheelDelta)
        {
            var currentOffset = _scrollViewer.VerticalOffset;
            if (!_timer.IsEnabled)
            {
                _targetOffset = currentOffset;
            }

            var wheelLines = SystemParameters.WheelScrollLines;
            var distance = wheelLines < 0
                ? _scrollViewer.ViewportHeight
                : Math.Max(48, wheelLines * 18);
            var requestedOffset = _targetOffset - (wheelDelta / 120d * distance);
            var nextTarget = Math.Clamp(requestedOffset, 0, _scrollViewer.ScrollableHeight);

            if (Math.Abs(nextTarget - _targetOffset) < 0.1)
            {
                return false;
            }

            _startOffset = currentOffset;
            _targetOffset = nextTarget;
            _startedAt = DateTime.UtcNow;
            _timer.Start();
            return true;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var progress = Math.Clamp(
                (DateTime.UtcNow - _startedAt).TotalMilliseconds / AnimationDuration.TotalMilliseconds,
                0,
                1);
            var easedProgress = 1 - Math.Pow(1 - progress, 3);
            _scrollViewer.ScrollToVerticalOffset(
                _startOffset + ((_targetOffset - _startOffset) * easedProgress));

            if (progress >= 1)
            {
                _timer.Stop();
            }
        }
    }
}
