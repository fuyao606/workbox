namespace Timenotifier.Models;

public sealed class EvidenceItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string WindowTitle { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string OcrText { get; set; } = "";
    public string? SourcePath { get; set; }
    public string? ThreadId { get; set; }
    public string? ImagePath { get; set; }
    public bool Included { get; set; } = true;
    public bool IsAutoCaptured { get; set; }
    public string ImageFingerprint { get; set; } = "";

    public string DisplayTime => CapturedAt.ToString("MM-dd HH:mm");
    public string DisplayTimelineTime => CapturedAt.ToString("HH:mm");
    public string DisplayKind => Kind switch
    {
        EvidenceKinds.CodexSession => "Codex",
        EvidenceKinds.TraeCnSession => "traecn",
        EvidenceKinds.DeepSeekHarnessSession => "DSH",
        EvidenceKinds.Screenshot => "截图",
        EvidenceKinds.ManualNote => "记录",
        _ => Kind
    };

    public string DisplayTags
    {
        get => string.Join("，", Tags);
        set => Tags = value
            .Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool CanPreviewImage =>
        Kind == EvidenceKinds.Screenshot
        && !string.IsNullOrWhiteSpace(ImagePath)
        && System.IO.File.Exists(ImagePath);

    public string DisplayWindowInfo
    {
        get
        {
            var parts = new[] { ProcessName, WindowTitle }
                .Where(item => !string.IsNullOrWhiteSpace(item));
            return string.Join(" · ", parts);
        }
    }
}

public static class EvidenceKinds
{
    public const string CodexSession = "CodexSession";
    public const string TraeCnSession = "TraeCnSession";
    public const string DeepSeekHarnessSession = "DeepSeekHarnessSession";
    public const string Screenshot = "Screenshot";
    public const string ManualNote = "ManualNote";

    public static bool IsAiSession(string kind)
    {
        return kind == CodexSession || kind == TraeCnSession || kind == DeepSeekHarnessSession;
    }
}

public sealed class EvidenceCaptureSettings
{
    public bool AutoScreenshotEnabled { get; set; }
    public int ScreenshotIntervalMinutes { get; set; } = 10;
    public string ScreenshotDirectory { get; set; } = "";
    public int MaxAutoScreenshots { get; set; } = 50;
    public string ManualNoteHotkey { get; set; } = "Ctrl+Alt+N";
    public bool StartWithWindows { get; set; }
    public bool OcrEnabled { get; set; }
    public List<string> PrivacyFilters { get; set; } = [];
    public List<string> DefaultTags { get; set; } = ["开发"];

    public string DisplayPrivacyFilters
    {
        get => string.Join(Environment.NewLine, PrivacyFilters);
        set => PrivacyFilters = SplitLines(value);
    }

    public string DisplayDefaultTags
    {
        get => string.Join("，", DefaultTags);
        set => DefaultTags = value
            .Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> SplitLines(string value)
    {
        return value
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
