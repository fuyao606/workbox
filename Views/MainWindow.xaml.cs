using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Timenotifier.Services.Infrastructure;

namespace Timenotifier.Views;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExNoActivate = 0x08000000;
    private const double ExpandedWidth = 116;
    private const double ExpandedHeight = 52;

    private WindowBounds? _boundsBeforeDock;

    private readonly DispatcherTimer _clockTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    public FloatingBallState FloatingState { get; private set; } = FloatingBallState.Expanded;
    public DockEdge? DockedEdge { get; private set; }
    public event EventHandler? ConfigurationRequested;
    public event EventHandler? ScreenshotRequested;

    public MainWindow()
    {
        InitializeComponent();

        Left = SystemParameters.WorkArea.Right - Width - 32;
        Top = SystemParameters.WorkArea.Top + 80;

        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        base.OnClosed(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HideFromWindowSwitcher();
    }

    private void HideFromWindowSwitcher()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        exStyle |= WsExToolWindow | WsExNoActivate;
        exStyle &= ~WsExAppWindow;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(exStyle));
    }

    private void UpdateClock()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            if (FloatingState == FloatingBallState.Docked && DockedEdge is not null)
            {
                ExpandFromDock();
                return;
            }

            DragMove();
            ClampToCurrentScreen();
        }
    }

    public void ShowExpanded()
    {
        Show();
        if (FloatingState == FloatingBallState.Docked && DockedEdge is not null)
        {
            ExpandFromDock();
        }
        else
        {
            ClampToCurrentScreen();
        }
    }

    public void DockToNearestOrRightEdge()
    {
        Show();
        HideToRightEdge();
    }

    public WindowBounds GetReminderAnchor()
    {
        var workArea = GetCurrentWorkArea();
        return FloatingBallDocking.GetPopupAnchor(CurrentBounds(), workArea, ExpandedSize(), FloatingState, DockedEdge);
    }

    public WorkAreaBounds GetCurrentWorkArea()
    {
        var scale = GetCurrentCoordinateScale();
        var deviceBounds = ScreenCoordinateMapper.DipsToDevicePixels(CurrentBounds(), scale);
        var screen = Screen.FromRectangle(new System.Drawing.Rectangle(
            (int)Math.Round(deviceBounds.Left),
            (int)Math.Round(deviceBounds.Top),
            Math.Max(1, (int)Math.Round(deviceBounds.Width)),
            Math.Max(1, (int)Math.Round(deviceBounds.Height))));

        return ScreenCoordinateMapper.DevicePixelsToDips(new WorkAreaBounds(
            screen.WorkingArea.Left,
            screen.WorkingArea.Top,
            screen.WorkingArea.Right,
            screen.WorkingArea.Bottom), scale);
    }

    private void SnapOrClampToCurrentScreen()
    {
        ApplyDockResult(FloatingBallDocking.SnapOrClamp(CurrentBounds(), GetCurrentWorkArea()));
    }

    private void ClampToCurrentScreen()
    {
        var result = FloatingBallDocking.SnapOrClamp(CurrentBounds(), GetCurrentWorkArea());
        if (result.State == FloatingBallState.Docked)
        {
            return;
        }

        ApplyDockResult(result);
    }

    private void HideToRightEdge()
    {
        if (FloatingState == FloatingBallState.Expanded)
        {
            var clamped = FloatingBallDocking.SnapOrClamp(CurrentBounds(), GetCurrentWorkArea());
            _boundsBeforeDock = new WindowBounds(clamped.Left, clamped.Top, ExpandedWidth, ExpandedHeight);
        }

        ApplyDockResult(FloatingBallDocking.HideToRightEdge(
            _boundsBeforeDock ?? CurrentBounds(),
            GetCurrentWorkArea()));
    }

    private void ExpandFromDock()
    {
        if (_boundsBeforeDock is WindowBounds previousBounds)
        {
            ApplyDockResult(new DockResult(
                FloatingBallState.Expanded,
                null,
                previousBounds.Left,
                previousBounds.Top,
                ExpandedWidth,
                ExpandedHeight));
            return;
        }

        if (DockedEdge is null)
        {
            return;
        }

        ApplyDockResult(FloatingBallDocking.ExpandFromDock(CurrentBounds(), GetCurrentWorkArea(), ExpandedSize(), DockedEdge.Value));
    }

    private void OnHideIconClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HideToRightEdge();
    }

    private void OnScreenshotIconClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ScreenshotRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnDockedIconClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ExpandFromDock();
    }

    private void OnIconMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ConfigurationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyDockResult(DockResult result)
    {
        FloatingState = result.State;
        DockedEdge = result.Edge;
        Width = result.Width;
        Height = result.Height;
        Left = result.Left;
        Top = result.Top;
        UpdateVisualState();
    }

    private WindowBounds CurrentBounds()
    {
        return new WindowBounds(Left, Top, Width, Height);
    }

    private static WindowSize ExpandedSize()
    {
        return new WindowSize(ExpandedWidth, ExpandedHeight);
    }

    private CoordinateScale GetCurrentCoordinateScale()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new CoordinateScale(dpi.DpiScaleX, dpi.DpiScaleY);
    }

    private void UpdateVisualState()
    {
        if (FloatingState == FloatingBallState.Docked)
        {
            ExpandedContent.Visibility = Visibility.Collapsed;
            DockedContent.Visibility = Visibility.Visible;
            FloatingSurface.Padding = new Thickness(0);
            FloatingSurface.Margin = new Thickness(0);
            FloatingSurface.CornerRadius = DockedEdge switch
            {
                DockEdge.Left => new CornerRadius(0, 10, 10, 0),
                DockEdge.Right => new CornerRadius(10, 0, 0, 10),
                DockEdge.Top => new CornerRadius(0, 0, 10, 10),
                DockEdge.Bottom => new CornerRadius(10, 10, 0, 0),
                _ => new CornerRadius(10)
            };
            DockedHandle.CornerRadius = DockedEdge switch
            {
                DockEdge.Left => new CornerRadius(0, 10, 10, 0),
                DockEdge.Right => new CornerRadius(10, 0, 0, 10),
                DockEdge.Top => new CornerRadius(0, 0, 10, 10),
                DockEdge.Bottom => new CornerRadius(10, 10, 0, 0),
                _ => new CornerRadius(10)
            };
            FloatingSurface.BorderThickness = new Thickness(0);
            return;
        }

        Width = ExpandedWidth;
        Height = ExpandedHeight;
        ExpandedContent.Visibility = Visibility.Visible;
        DockedContent.Visibility = Visibility.Collapsed;
        FloatingSurface.Padding = new Thickness(0);
        FloatingSurface.Margin = new Thickness(0);
        FloatingSurface.CornerRadius = new CornerRadius(0);
        FloatingSurface.BorderThickness = new Thickness(0);
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
