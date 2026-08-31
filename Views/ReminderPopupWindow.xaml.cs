using System.Windows;
using System.Windows.Threading;
using Timenotifier.Services.Infrastructure;

namespace Timenotifier.Views;

public partial class ReminderPopupWindow : Window
{
    private readonly DispatcherTimer _closeTimer = new()
    {
        Interval = TimeSpan.FromSeconds(18)
    };

    public ReminderPopupWindow(
        string message,
        string title = "时间提醒")
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;

        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            Close();
        };
    }

    public void PlaceNear(WindowBounds anchor, WorkAreaBounds workArea)
    {
        var preferredLeft = anchor.Left + anchor.Width + 12;
        if (preferredLeft + Width > workArea.Right - 12)
        {
            preferredLeft = anchor.Left - Width - 12;
        }

        Left = Math.Min(Math.Max(preferredLeft, workArea.Left + 12), workArea.Right - Width - 12);
        Top = Math.Min(Math.Max(anchor.Top + 18, workArea.Top + 12), workArea.Bottom - Height - 12);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _closeTimer.Start();
    }

    private void OnPopupMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        _closeTimer.Stop();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closeTimer.Stop();
        base.OnClosed(e);
    }
}
