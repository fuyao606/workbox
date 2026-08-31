using System.Windows.Threading;
using Timenotifier.Models;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Settings;

namespace Timenotifier.Services.Scheduling;

public sealed class AutoScreenshotScheduler(
    IAppSettingsStore settingsStore,
    IScreenshotCaptureService screenshotCapture,
    Action<string> notify)
{
    private readonly DispatcherTimer _timer = new();
    private EvidenceCaptureSettings _settings = new();
    private bool _isCapturing;
    private bool _started;

    public async Task StartAsync()
    {
        if (!_started)
        {
            _timer.Tick += async (_, _) => await CaptureAsync();
            _started = true;
        }

        await ReloadAsync();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public async Task ReloadAsync()
    {
        _settings = await settingsStore.LoadEvidenceCaptureSettingsAsync();
        _timer.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.ScreenshotIntervalMinutes));
        if (_settings.AutoScreenshotEnabled)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private async Task CaptureAsync()
    {
        if (_isCapturing)
        {
            return;
        }

        _isCapturing = true;
        try
        {
            var evidence = await screenshotCapture.CaptureFullScreenAsync(_settings, isAutoCaptured: true);
            if (evidence is not null)
            {
                notify($"已自动记录截图：{evidence.CapturedAt:HH:mm:ss}");
            }
        }
        catch (Exception ex)
        {
            notify($"自动截图失败：{ex.Message}");
        }
        finally
        {
            _isCapturing = false;
        }
    }
}
