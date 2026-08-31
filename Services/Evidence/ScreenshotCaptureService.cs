using System.IO;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Timenotifier.Models;
using Timenotifier.Services.Infrastructure;

namespace Timenotifier.Services.Evidence;

public interface IScreenshotCaptureService
{
    Task<EvidenceItem> CaptureFullScreenAsync(CancellationToken cancellationToken = default);
    Task<EvidenceItem?> CaptureFullScreenAsync(
        EvidenceCaptureSettings settings,
        bool isAutoCaptured,
        CancellationToken cancellationToken = default);
}

public sealed class ScreenshotCaptureService(
    AppDataPaths paths,
    IEvidenceStore evidenceStore,
    ITimeProvider timeProvider,
    IActiveWindowService? activeWindowService = null,
    ILocalOcrService? ocrService = null)
    : IScreenshotCaptureService
{
    private readonly IActiveWindowService _activeWindowService = activeWindowService ?? new ActiveWindowService();
    private readonly ILocalOcrService _ocrService = ocrService ?? new LocalOcrService();
    private readonly ScreenshotChangeDetector _changeDetector = new();
    private string? _lastAutoFingerprint;

    public async Task<EvidenceItem> CaptureFullScreenAsync(CancellationToken cancellationToken = default)
    {
        var evidence = await CaptureFullScreenAsync(new EvidenceCaptureSettings
        {
            ScreenshotDirectory = paths.ScreenshotRootDirectory,
            DefaultTags = ["开发"]
        }, isAutoCaptured: false, cancellationToken);
        return evidence ?? throw new InvalidOperationException("截图未保存。");
    }

    public async Task<EvidenceItem?> CaptureFullScreenAsync(
        EvidenceCaptureSettings settings,
        bool isAutoCaptured,
        CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var now = timeProvider.Now;
        var date = DateOnly.FromDateTime(now);
        var rootDirectory = string.IsNullOrWhiteSpace(settings.ScreenshotDirectory)
            ? paths.ScreenshotRootDirectory
            : settings.ScreenshotDirectory;
        var directory = Path.Combine(rootDirectory, $"{date:yyyy-MM-dd}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, $"{now:HH-mm-ss}.png");
        var activeWindow = _activeWindowService.GetActiveWindow();

        if (isAutoCaptured && IsPrivacyFiltered(activeWindow, settings.PrivacyFilters))
        {
            return null;
        }

        var bounds = Screen.AllScreens
            .Select(screen => screen.Bounds)
            .Aggregate(System.Drawing.Rectangle.Union);

        using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        }

        var fingerprint = _changeDetector.CreateFingerprint(bitmap);
        if (isAutoCaptured && !_changeDetector.HasMeaningfulChange(_lastAutoFingerprint, fingerprint))
        {
            return null;
        }

        bitmap.Save(imagePath, ImageFormat.Png);
        if (isAutoCaptured)
        {
            _lastAutoFingerprint = fingerprint;
        }

        var ocrText = "";
        if (settings.OcrEnabled)
        {
            try
            {
                ocrText = await _ocrService.TryExtractTextAsync(imagePath, cancellationToken);
            }
            catch (Exception ex)
            {
                ocrText = $"OCR 失败：{ex.Message}";
            }
        }

        var evidence = new EvidenceItem
        {
            Id = $"screenshot:{now:yyyyMMddHHmmss}:{Guid.NewGuid():N}",
            Kind = EvidenceKinds.Screenshot,
            CapturedAt = now,
            Title = string.IsNullOrWhiteSpace(activeWindow.Title)
                ? $"工作截图 {now:HH:mm:ss}"
                : $"{activeWindow.Title} - {now:HH:mm:ss}",
            Summary = BuildSummary(isAutoCaptured, activeWindow, ocrText),
            Tags = settings.DefaultTags.ToList(),
            WindowTitle = activeWindow.Title,
            ProcessName = activeWindow.ProcessName,
            OcrText = ocrText,
            ImagePath = imagePath,
            SourcePath = imagePath,
            Included = true,
            IsAutoCaptured = isAutoCaptured,
            ImageFingerprint = fingerprint
        };

        await evidenceStore.UpsertEvidenceAsync([evidence], cancellationToken);
        if (isAutoCaptured)
        {
            await evidenceStore.PruneAutoScreenshotEvidenceAsync(settings.MaxAutoScreenshots, cancellationToken);
        }

        return evidence;
    }

    private static bool IsPrivacyFiltered(ActiveWindowInfo activeWindow, IReadOnlyList<string> filters)
    {
        if (filters.Count == 0)
        {
            return false;
        }

        var target = $"{activeWindow.ProcessName} {activeWindow.Title}";
        return filters.Any(filter => target.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSummary(bool isAutoCaptured, ActiveWindowInfo activeWindow, string ocrText)
    {
        List<string> parts =
        [
            isAutoCaptured ? "自动保存的变化截图。" : "用户主动保存的全屏工作截图。"
        ];
        if (!string.IsNullOrWhiteSpace(activeWindow.ProcessName) || !string.IsNullOrWhiteSpace(activeWindow.Title))
        {
            parts.Add($"活跃窗口：{activeWindow.ProcessName} {activeWindow.Title}".Trim());
        }

        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            parts.Add($"OCR：{Trim(ocrText, 240)}");
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string Trim(string value, int maxLength)
    {
        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }
}
