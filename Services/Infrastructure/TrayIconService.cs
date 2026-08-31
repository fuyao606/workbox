using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Timenotifier.Services.Infrastructure;

public sealed class TrayIconService : IDisposable
{
    private static readonly Color SurfaceColor = Color.FromArgb(248, 250, 252);
    private static readonly Color TextColor = Color.FromArgb(31, 41, 55);
    private static readonly Color MutedTextColor = Color.FromArgb(102, 112, 133);
    private static readonly Color AccentColor = Color.FromArgb(30, 168, 133);

    private readonly Icon _appIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleFloatingBallMenuItem;
    private readonly Action _showFloatingBall;
    private readonly Action _hideFloatingBall;
    private readonly Func<bool> _isFloatingBallVisible;
    private readonly Image _showFloatingBallImage;
    private readonly Image _hideFloatingBallImage;

    public TrayIconService(
        Action showFloatingBall,
        Action hideFloatingBall,
        Func<bool> isFloatingBallVisible,
        Action openConfiguration,
        Action exitApplication)
    {
        _showFloatingBall = showFloatingBall;
        _hideFloatingBall = hideFloatingBall;
        _isFloatingBallVisible = isFloatingBallVisible;
        _appIcon = LoadAppIcon();
        _showFloatingBallImage = CreateCircleImage(Color.FromArgb(32, 160, 120));
        _hideFloatingBallImage = CreateCircleImage(Color.FromArgb(111, 118, 130));
        _contextMenu = CreateContextMenu();
        _toggleFloatingBallMenuItem = CreateMenuItem("隐藏悬浮球", _hideFloatingBallImage, ToggleFloatingBall);
        _contextMenu.Opening += (_, _) => UpdateFloatingBallMenuItem();
        _contextMenu.Items.Add(_toggleFloatingBallMenuItem);
        _contextMenu.Items.Add(CreateMenuItem("配置中心", CreateCircleImage(Color.FromArgb(79, 122, 255)), openConfiguration));
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(CreateMenuItem("退出", CreateCircleImage(Color.FromArgb(232, 92, 78)), exitApplication));

        _notifyIcon = new NotifyIcon
        {
            Text = "Timenotifier",
            Icon = _appIcon,
            ContextMenuStrip = _contextMenu,
            Visible = false
        };

        _notifyIcon.DoubleClick += (_, _) => _showFloatingBall();
    }

    public void Show()
    {
        _notifyIcon.Visible = true;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _showFloatingBallImage.Dispose();
        _hideFloatingBallImage.Dispose();
        _appIcon.Dispose();
    }

    private void ToggleFloatingBall()
    {
        if (_isFloatingBallVisible())
        {
            _hideFloatingBall();
            return;
        }

        _showFloatingBall();
    }

    private void UpdateFloatingBallMenuItem()
    {
        if (_isFloatingBallVisible())
        {
            _toggleFloatingBallMenuItem.Text = "隐藏悬浮球";
            _toggleFloatingBallMenuItem.Image = _hideFloatingBallImage;
            return;
        }

        _toggleFloatingBallMenuItem.Text = "显示悬浮球";
        _toggleFloatingBallMenuItem.Image = _showFloatingBallImage;
    }

    private static ContextMenuStrip CreateContextMenu()
    {
        return new ContextMenuStrip
        {
            BackColor = SurfaceColor,
            ForeColor = TextColor,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            Padding = new Padding(7, 7, 7, 7),
            ShowImageMargin = true,
            Renderer = new TimenotifierMenuRenderer()
        };
    }

    private static ToolStripMenuItem CreateMenuItem(string text, Image image, Action action)
    {
        return new ToolStripMenuItem(text, image, (_, _) => action())
        {
            AutoSize = false,
            Height = 34,
            Width = 178,
            Padding = new Padding(8, 0, 10, 0),
            ForeColor = TextColor
        };
    }

    private static Bitmap CreateCircleImage(Color color)
    {
        var bitmap = new Bitmap(18, 18);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(Color.FromArgb(32, color));
        using var foreground = new SolidBrush(color);
        graphics.FillEllipse(background, 1, 1, 16, 16);
        graphics.FillEllipse(foreground, 5, 5, 8, 8);
        return bitmap;
    }

    private static Icon LoadAppIcon()
    {
        var resource = global::System.Windows.Application.GetResourceStream(new Uri("Assets/Timenotifier.ico", UriKind.Relative));
        if (resource is null)
        {
            return CreateAppIcon();
        }

        using var stream = resource.Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var outerBrush = new LinearGradientBrush(
            new Rectangle(2, 2, 28, 28),
            Color.FromArgb(45, 199, 150),
            Color.FromArgb(47, 91, 225),
            LinearGradientMode.ForwardDiagonal);
        using var shadowBrush = new SolidBrush(Color.FromArgb(45, 15, 23, 42));
        using var faceBrush = new SolidBrush(Color.FromArgb(252, 255, 255, 255));
        using var handPen = new Pen(Color.FromArgb(28, 42, 62), 2.2F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var accentPen = new Pen(Color.FromArgb(45, 199, 150), 2F);

        graphics.FillEllipse(shadowBrush, 4, 5, 25, 25);
        graphics.FillEllipse(outerBrush, 2, 2, 28, 28);
        graphics.FillEllipse(faceBrush, 7, 7, 18, 18);
        graphics.DrawEllipse(accentPen, 7, 7, 18, 18);
        graphics.DrawLine(handPen, 16, 16, 16, 10);
        graphics.DrawLine(handPen, 16, 16, 21, 18);
        graphics.FillEllipse(new SolidBrush(Color.FromArgb(28, 42, 62)), 14, 14, 4, 4);

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private sealed class TimenotifierMenuRenderer : ToolStripProfessionalRenderer
    {
        public TimenotifierMenuRenderer()
            : base(new TimenotifierMenuColorTable())
        {
            RoundedEdges = true;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected)
            {
                using var brush = new SolidBrush(Color.FromArgb(235, 247, 243));
                using var borderPen = new Pen(Color.FromArgb(199, 235, 224));
                var bounds = new Rectangle(4, 2, e.Item.Width - 8, e.Item.Height - 4);
                using var path = RoundedRectangle(bounds, 8);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(borderPen, path);
                return;
            }

            base.OnRenderMenuItemBackground(e);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.FromArgb(16, 120, 92) : TextColor;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(229, 231, 235));
            e.Graphics.DrawLine(pen, 34, 4, e.Item.Width - 8, 4);
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class TimenotifierMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => SurfaceColor;
        public override Color ImageMarginGradientBegin => SurfaceColor;
        public override Color ImageMarginGradientMiddle => SurfaceColor;
        public override Color ImageMarginGradientEnd => SurfaceColor;
        public override Color MenuBorder => Color.FromArgb(218, 225, 232);
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Color.FromArgb(235, 247, 243);
        public override Color SeparatorDark => Color.FromArgb(229, 231, 235);
        public override Color SeparatorLight => Color.White;
    }
}
