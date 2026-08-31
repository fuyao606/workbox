using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Timenotifier.Models;
using Timenotifier.Services.Automation;
using Timenotifier.Services.Infrastructure;

namespace Timenotifier.Views;

public partial class AutomationReviewWindow : Window
{
    private readonly DailyReportAutomationService _automationService;
    private readonly DailyReportAutomationResult _result;
    private bool _submitted;

    public AutomationReviewWindow(
        DailyReportAutomationService automationService,
        DailyReportAutomationResult result)
    {
        _automationService = automationService;
        _result = result;
        InitializeComponent();
        StatusText.Text = result.StatusMessage;
        DailyTextBox.Text = result.DailyText;
        LoadImage(result.BeforeSubmitScreenshotPath);
    }

    private async void OnConfirmClicked(object sender, RoutedEventArgs e)
    {
        if (_submitted)
        {
            Close();
            return;
        }

        try
        {
            ConfirmButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            StatusText.Text = "正在同步编辑后的文本并提交日报...";
            var editedDailyText = DailyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(editedDailyText))
            {
                throw new InvalidOperationException("日报纯文本不能为空。");
            }

            await _automationService.SubmitAsync(_result, editedDailyText);
            _submitted = true;
            ScreenshotTitleText.Text = "提交后截图";
            StatusText.Text = _result.StatusMessage;
            LoadImage(_result.AfterSubmitScreenshotPath);
            ConfirmButton.Content = "关闭";
            ConfirmButton.IsEnabled = true;
            DailyTextBox.IsReadOnly = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"提交失败：{ResponsesModelClient.FlattenException(ex)}";
            ConfirmButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LoadImage(string path)
    {
        if (!File.Exists(path))
        {
            StatusText.Text = $"截图文件不存在：{path}";
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        ScreenshotImage.Source = image;
    }
}
