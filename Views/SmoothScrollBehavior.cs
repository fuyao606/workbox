using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text.Json;

namespace Timenotifier.Views;

public static class SmoothScrollBehavior
{
    private const double WheelDelta = 120d;
    private const double MinimumStep = 36d;
    private const double DefaultStep = 54d;
    private static readonly object DiagnosticsLock = new();
    private static readonly string? ScrollDiagnosticsPath = GetScrollDiagnosticsPath();

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(SmoothScrollState),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static SmoothScrollState? GetState(DependencyObject element) =>
        (SmoothScrollState?)element.GetValue(StateProperty);

    private static void SetState(DependencyObject element, SmoothScrollState? value) =>
        element.SetValue(StateProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ScrollViewer scrollViewer)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            if (GetState(scrollViewer) is not null)
            {
                return;
            }

            SetState(scrollViewer, new SmoothScrollState(scrollViewer));
            scrollViewer.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel), true);
            scrollViewer.ScrollChanged += OnScrollChanged;
            return;
        }

        scrollViewer.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
        scrollViewer.ScrollChanged -= OnScrollChanged;
        GetState(scrollViewer)?.Dispose();
        SetState(scrollViewer, null);
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (sender is not ScrollViewer scrollViewer || string.IsNullOrWhiteSpace(ScrollDiagnosticsPath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(ScrollDiagnosticsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var record = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                automationId = AutomationProperties.GetAutomationId(scrollViewer),
                verticalOffset = scrollViewer.VerticalOffset,
                scrollableHeight = scrollViewer.ScrollableHeight
            });
            lock (DiagnosticsLock)
            {
                File.AppendAllText(ScrollDiagnosticsPath, record + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics are opt-in and must never affect normal scrolling.
        }
    }

    private static string? GetScrollDiagnosticsPath()
    {
        const string prefix = "--ui-loop-scroll-inspection-path=";
        var argument = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return argument is null ? null : argument[prefix.Length..];
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (sender is not ScrollViewer scrollViewer || args.Handled || args.Delta == 0)
        {
            return;
        }

        var state = GetState(scrollViewer);
        if (state is null || !state.CanScroll(args.Delta))
        {
            return;
        }

        if (FindNestedScrollableViewer(args.OriginalSource as DependencyObject, scrollViewer, args.Delta) is not null)
        {
            return;
        }

        var step = SystemParameters.WheelScrollLines > 0
            ? Math.Max(MinimumStep, SystemParameters.WheelScrollLines * 18d)
            : DefaultStep;

        state.ScrollBy(-args.Delta / WheelDelta * step);
        args.Handled = true;
    }

    private static ScrollViewer? FindNestedScrollableViewer(DependencyObject? source, ScrollViewer owner, int wheelDelta)
    {
        for (var current = source; current is not null && current != owner; current = GetParent(current))
        {
            if (current is ScrollViewer scrollViewer && CanScroll(scrollViewer, wheelDelta))
            {
                return scrollViewer;
            }
        }

        return null;
    }

    private static bool CanScroll(ScrollViewer scrollViewer, int wheelDelta)
    {
        const double tolerance = 0.5d;
        return wheelDelta < 0
            ? scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight - tolerance
            : scrollViewer.VerticalOffset > tolerance;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        try
        {
            return VisualTreeHelper.GetParent(element);
        }
        catch (InvalidOperationException)
        {
            return LogicalTreeHelper.GetParent(element);
        }
    }

    private sealed class SmoothScrollState : IDisposable
    {
        private const double StopThreshold = 0.5d;
        private const double InterpolationFactor = 0.32d;
        private readonly ScrollViewer _scrollViewer;
        private readonly DispatcherTimer _timer;
        private double _targetOffset;

        public SmoothScrollState(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;
            _timer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _timer.Tick += OnTimerTick;
        }

        public bool CanScroll(int wheelDelta)
        {
            var offset = _timer.IsEnabled ? _targetOffset : _scrollViewer.VerticalOffset;
            const double tolerance = 0.5d;
            return wheelDelta < 0
                ? offset < _scrollViewer.ScrollableHeight - tolerance
                : offset > tolerance;
        }

        public void ScrollBy(double amount)
        {
            var startOffset = _timer.IsEnabled ? _targetOffset : _scrollViewer.VerticalOffset;
            _targetOffset = Math.Clamp(startOffset + amount, 0d, _scrollViewer.ScrollableHeight);

            if (Math.Abs(_targetOffset - _scrollViewer.VerticalOffset) <= StopThreshold)
            {
                _scrollViewer.ScrollToVerticalOffset(_targetOffset);
                return;
            }

            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
        }

        private void OnTimerTick(object? sender, EventArgs args)
        {
            _targetOffset = Math.Clamp(_targetOffset, 0d, _scrollViewer.ScrollableHeight);
            var distance = _targetOffset - _scrollViewer.VerticalOffset;

            if (Math.Abs(distance) <= StopThreshold)
            {
                _scrollViewer.ScrollToVerticalOffset(_targetOffset);
                _timer.Stop();
                return;
            }

            _scrollViewer.ScrollToVerticalOffset(_scrollViewer.VerticalOffset + distance * InterpolationFactor);
        }
    }
}
