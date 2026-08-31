using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Timenotifier.Models;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Scheduling;

namespace Timenotifier.Services.Settings;

public interface IAppSettingsStore
{
    Task<ModelProviderSettings> LoadModelSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveModelSettingsAsync(ModelProviderSettings settings, CancellationToken cancellationToken = default);
    Task<ReminderSettings> LoadReminderSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveReminderSettingsAsync(ReminderSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportTemplate>> LoadReportTemplatesAsync(CancellationToken cancellationToken = default);
    Task SaveReportTemplatesAsync(IReadOnlyList<ReportTemplate> templates, CancellationToken cancellationToken = default);
    Task<EvidenceCaptureSettings> LoadEvidenceCaptureSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveEvidenceCaptureSettingsAsync(EvidenceCaptureSettings settings, CancellationToken cancellationToken = default);
    Task<DailyReportAutomationSettings> LoadDailyReportAutomationSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveDailyReportAutomationSettingsAsync(DailyReportAutomationSettings settings, CancellationToken cancellationToken = default);
}

public sealed class AppSettingsStore(AppDataPaths paths) : IAppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Timenotifier.ModelProviderSettings.v1");
    private readonly SemaphoreSlim _settingsLock = new(1, 1);

    public async Task<ModelProviderSettings> LoadModelSettingsAsync(CancellationToken cancellationToken = default)
    {
        var persisted = await LoadPersistedSettingsAsync(cancellationToken);

        return new ModelProviderSettings
        {
            Analysis = new SingleModelProviderSettings
            {
                BaseUrl = FirstNonEmpty(
                    persisted.ModelProvider.Analysis.BaseUrl,
                    persisted.ModelProvider.Text.BaseUrl,
                    persisted.ModelProvider.BaseUrl).Trim(),
                ApiKey = FirstNonEmpty(
                    Unprotect(persisted.ModelProvider.Analysis.ProtectedApiKey),
                    Unprotect(persisted.ModelProvider.Text.ProtectedApiKey),
                    Unprotect(persisted.ModelProvider.ProtectedApiKey)).Trim(),
                Model = FirstNonEmpty(
                    persisted.ModelProvider.Analysis.Model,
                    persisted.ModelProvider.Text.Model,
                    persisted.ModelProvider.TextModel,
                    persisted.ModelProvider.Model).Trim()
            },
            ImageGeneration = new SingleModelProviderSettings
            {
                BaseUrl = FirstNonEmpty(
                    persisted.ModelProvider.ImageGeneration.BaseUrl,
                    persisted.ModelProvider.Vision.BaseUrl).Trim(),
                ApiKey = FirstNonEmpty(
                    Unprotect(persisted.ModelProvider.ImageGeneration.ProtectedApiKey),
                    Unprotect(persisted.ModelProvider.Vision.ProtectedApiKey)).Trim(),
                Model = FirstNonEmpty(
                    persisted.ModelProvider.ImageGeneration.Model,
                    persisted.ModelProvider.Vision.Model,
                    persisted.ModelProvider.VisionModel).Trim()
            },
            UseVision = persisted.ModelProvider.UseVision,
            QualityMode = persisted.ModelProvider.QualityMode
        };
    }

    public async Task SaveModelSettingsAsync(ModelProviderSettings settings, CancellationToken cancellationToken = default)
    {
        await UpdatePersistedSettingsAsync(persisted =>
        {
            persisted.ModelProvider = new PersistedModelProviderSettings
            {
                BaseUrl = settings.Analysis.BaseUrl.Trim(),
                ProtectedApiKey = Protect(settings.Analysis.ApiKey.Trim()),
                Model = settings.Analysis.Model.Trim(),
                TextModel = settings.Analysis.Model.Trim(),
                VisionModel = settings.ImageGeneration.Model.Trim(),
                UseVision = settings.UseVision,
                QualityMode = settings.QualityMode,
                Analysis = new PersistedSingleModelProviderSettings
                {
                    BaseUrl = settings.Analysis.BaseUrl.Trim(),
                    ProtectedApiKey = Protect(settings.Analysis.ApiKey.Trim()),
                    Model = settings.Analysis.Model.Trim()
                },
                ImageGeneration = new PersistedSingleModelProviderSettings
                {
                    BaseUrl = settings.ImageGeneration.BaseUrl.Trim(),
                    ProtectedApiKey = Protect(settings.ImageGeneration.ApiKey.Trim()),
                    Model = settings.ImageGeneration.Model.Trim()
                },
                Text = new PersistedSingleModelProviderSettings
                {
                    BaseUrl = settings.Analysis.BaseUrl.Trim(),
                    ProtectedApiKey = Protect(settings.Analysis.ApiKey.Trim()),
                    Model = settings.Analysis.Model.Trim()
                },
                Vision = new PersistedSingleModelProviderSettings
                {
                    BaseUrl = settings.ImageGeneration.BaseUrl.Trim(),
                    ProtectedApiKey = Protect(settings.ImageGeneration.ApiKey.Trim()),
                    Model = settings.ImageGeneration.Model.Trim()
                }
            };
        }, cancellationToken);
    }

    public async Task<ReminderSettings> LoadReminderSettingsAsync(CancellationToken cancellationToken = default)
    {
        var persisted = await LoadPersistedSettingsAsync(cancellationToken);
        return NormalizeReminderSettings(persisted.Reminders);
    }

    public async Task SaveReminderSettingsAsync(ReminderSettings settings, CancellationToken cancellationToken = default)
    {
        await UpdatePersistedSettingsAsync(
            persisted => persisted.Reminders = NormalizeReminderSettings(settings),
            cancellationToken);
    }

    public async Task<IReadOnlyList<ReportTemplate>> LoadReportTemplatesAsync(CancellationToken cancellationToken = default)
    {
        return await UpdatePersistedSettingsAsync(persisted =>
        {
            persisted.ReportTemplates = NormalizeReportTemplates(persisted.ReportTemplates);
            return persisted.ReportTemplates;
        }, cancellationToken);
    }

    public async Task SaveReportTemplatesAsync(IReadOnlyList<ReportTemplate> templates, CancellationToken cancellationToken = default)
    {
        await UpdatePersistedSettingsAsync(
            persisted => persisted.ReportTemplates = NormalizeReportTemplates(templates.ToList()),
            cancellationToken);
    }

    public async Task<EvidenceCaptureSettings> LoadEvidenceCaptureSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await UpdatePersistedSettingsAsync(persisted =>
        {
            persisted.EvidenceCapture = NormalizeEvidenceCaptureSettings(persisted.EvidenceCapture);
            return persisted.EvidenceCapture;
        }, cancellationToken);
    }

    public async Task SaveEvidenceCaptureSettingsAsync(EvidenceCaptureSettings settings, CancellationToken cancellationToken = default)
    {
        await UpdatePersistedSettingsAsync(
            persisted => persisted.EvidenceCapture = NormalizeEvidenceCaptureSettings(settings),
            cancellationToken);
    }

    public async Task<DailyReportAutomationSettings> LoadDailyReportAutomationSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await UpdatePersistedSettingsAsync(persisted =>
        {
            persisted.DailyReportAutomation = NormalizeDailyReportAutomationSettings(persisted.DailyReportAutomation);
            return persisted.DailyReportAutomation;
        }, cancellationToken);
    }

    public async Task SaveDailyReportAutomationSettingsAsync(DailyReportAutomationSettings settings, CancellationToken cancellationToken = default)
    {
        await UpdatePersistedSettingsAsync(
            persisted => persisted.DailyReportAutomation = NormalizeDailyReportAutomationSettings(settings),
            cancellationToken);
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return "";
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedValue);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return "";
        }
        catch (FormatException)
        {
            return "";
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private async Task<PersistedSettings> LoadPersistedSettingsAsync(CancellationToken cancellationToken)
    {
        await _settingsLock.WaitAsync(cancellationToken);
        try
        {
            return await LoadPersistedSettingsCoreAsync(cancellationToken);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private async Task UpdatePersistedSettingsAsync(Action<PersistedSettings> update, CancellationToken cancellationToken)
    {
        await UpdatePersistedSettingsAsync(
            persisted =>
            {
                update(persisted);
                return true;
            },
            cancellationToken);
    }

    private async Task<T> UpdatePersistedSettingsAsync<T>(Func<PersistedSettings, T> update, CancellationToken cancellationToken)
    {
        await _settingsLock.WaitAsync(cancellationToken);
        try
        {
            var persisted = await LoadPersistedSettingsCoreAsync(cancellationToken);
            var result = update(persisted);
            await SavePersistedSettingsCoreAsync(persisted, cancellationToken);
            return result;
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private async Task<PersistedSettings> LoadPersistedSettingsCoreAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        if (!File.Exists(paths.SettingsPath))
        {
            return new PersistedSettings();
        }

        await using var stream = new FileStream(
            paths.SettingsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<PersistedSettings>(stream, JsonOptions, cancellationToken)
            ?? new PersistedSettings();
    }

    private async Task SavePersistedSettingsCoreAsync(PersistedSettings persisted, CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        await using var stream = new FileStream(
            paths.SettingsPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, persisted, JsonOptions, cancellationToken);
    }

    private static ReminderSettings NormalizeReminderSettings(ReminderSettings? settings)
    {
        settings ??= DefaultReminders.CreateSettings();
        if (settings.Rules.Count == 0)
        {
            settings = DefaultReminders.CreateSettings();
        }

        settings.SnoozeMinutes = Math.Clamp(settings.SnoozeMinutes <= 0 ? 10 : settings.SnoozeMinutes, 1, 240);
        foreach (var rule in settings.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                rule.Id = Guid.NewGuid().ToString("N");
            }

            rule.Message = string.IsNullOrWhiteSpace(rule.Message) ? "时间提醒" : rule.Message.Trim();
            rule.AdvanceMinutes = Math.Clamp(rule.AdvanceMinutes, 0, 1440);
            if (rule.ScheduleKind != ReminderScheduleKind.SpecificWeekdays)
            {
                rule.Weekdays.Clear();
            }
        }

        foreach (var adjustment in settings.HolidayAdjustments)
        {
            adjustment.Name = adjustment.Name.Trim();
        }

        settings.HolidayAdjustments = settings.HolidayAdjustments
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Kind)
            .ToList();
        return settings;
    }

    private static List<ReportTemplate> NormalizeReportTemplates(List<ReportTemplate>? templates)
    {
        templates ??= [];
        var byId = templates
            .Where(template => !string.IsNullOrWhiteSpace(template.Id))
            .GroupBy(template => template.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToDictionary(template => template.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var builtIn in CreateDefaultReportTemplates())
        {
            if (!byId.ContainsKey(builtIn.Id))
            {
                byId[builtIn.Id] = builtIn;
            }
        }

        foreach (var template in byId.Values)
        {
            if (string.IsNullOrWhiteSpace(template.Id))
            {
                template.Id = Guid.NewGuid().ToString("N");
            }

            template.Name = string.IsNullOrWhiteSpace(template.Name) ? "未命名模板" : template.Name.Trim();
            template.PromptInstructions = template.PromptInstructions.Trim();
            if (template.ReportTypeScope is not ReportTemplateScopes.Daily
                and not ReportTemplateScopes.Weekly
                and not ReportTemplateScopes.Both)
            {
                template.ReportTypeScope = ReportTemplateScopes.Both;
            }
        }

        return byId.Values
            .OrderByDescending(template => template.IsBuiltIn)
            .ThenBy(template => template.Name)
            .ToList();
    }

    public static List<ReportTemplate> CreateDefaultReportTemplates()
    {
        return
        [
            new ReportTemplate
            {
                Id = "built-in-dev",
                Name = "研发",
                ReportTypeScope = ReportTemplateScopes.Both,
                IsBuiltIn = true,
                PromptInstructions = "突出需求实现、缺陷修复、代码评审、测试验证、技术风险和下一步研发计划。"
            },
            new ReportTemplate
            {
                Id = "built-in-qa",
                Name = "测试",
                ReportTypeScope = ReportTemplateScopes.Both,
                IsBuiltIn = true,
                PromptInstructions = "突出测试范围、用例设计、缺陷发现与回归结果、阻塞问题和质量风险。"
            },
            new ReportTemplate
            {
                Id = "built-in-product",
                Name = "产品",
                ReportTypeScope = ReportTemplateScopes.Both,
                IsBuiltIn = true,
                PromptInstructions = "突出需求梳理、方案决策、用户反馈、优先级变化、协作事项和待确认问题。"
            },
            new ReportTemplate
            {
                Id = "built-in-ops",
                Name = "运营",
                ReportTypeScope = ReportTemplateScopes.Both,
                IsBuiltIn = true,
                PromptInstructions = "突出运营动作、数据观察、活动推进、渠道协作、异常问题和后续跟进。"
            }
        ];
    }

    private EvidenceCaptureSettings NormalizeEvidenceCaptureSettings(EvidenceCaptureSettings? settings)
    {
        settings ??= new EvidenceCaptureSettings();
        settings.ScreenshotIntervalMinutes = Math.Clamp(settings.ScreenshotIntervalMinutes <= 0 ? 10 : settings.ScreenshotIntervalMinutes, 1, 240);
        settings.MaxAutoScreenshots = Math.Clamp(settings.MaxAutoScreenshots <= 0 ? 50 : settings.MaxAutoScreenshots, 1, 1000);
        settings.ManualNoteHotkey = string.IsNullOrWhiteSpace(settings.ManualNoteHotkey)
            ? "Ctrl+Alt+N"
            : settings.ManualNoteHotkey.Trim();
        settings.ScreenshotDirectory = string.IsNullOrWhiteSpace(settings.ScreenshotDirectory)
            ? paths.ScreenshotRootDirectory
            : Environment.ExpandEnvironmentVariables(settings.ScreenshotDirectory.Trim());
        settings.PrivacyFilters = settings.PrivacyFilters
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.DefaultTags = settings.DefaultTags.Count == 0
            ? ["开发"]
            : settings.DefaultTags
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        return settings;
    }

    private DailyReportAutomationSettings NormalizeDailyReportAutomationSettings(DailyReportAutomationSettings? settings)
    {
        settings ??= new DailyReportAutomationSettings();
        settings.TargetUrl = string.IsNullOrWhiteSpace(settings.TargetUrl)
            ? DailyReportAutomationSettings.DefaultTargetUrl
            : settings.TargetUrl.Trim();
        settings.BrowserSessionName = string.IsNullOrWhiteSpace(settings.BrowserSessionName)
            ? "timenotifier-daily-report"
            : settings.BrowserSessionName.Trim();
        settings.BrowserProfileDirectory = string.IsNullOrWhiteSpace(settings.BrowserProfileDirectory)
            ? paths.AutomationBrowserProfileDirectory
            : Environment.ExpandEnvironmentVariables(settings.BrowserProfileDirectory.Trim());
        settings.ProjectName = string.IsNullOrWhiteSpace(settings.ProjectName)
            ? DailyReportAutomationSettings.DefaultProjectName
            : settings.ProjectName.Trim();
        settings.TaskName = string.IsNullOrWhiteSpace(settings.TaskName)
            ? DailyReportAutomationSettings.DefaultTaskName
            : settings.TaskName.Trim();
        settings.TaskType = string.IsNullOrWhiteSpace(settings.TaskType)
            ? DailyReportAutomationSettings.DefaultTaskType
            : settings.TaskType.Trim();
        settings.WorkloadPercent = Math.Clamp(settings.WorkloadPercent <= 0 ? 100 : settings.WorkloadPercent, 1, 100);
        return settings;
    }

    private sealed class PersistedSettings
    {
        public PersistedModelProviderSettings ModelProvider { get; set; } = new();
        public ReminderSettings Reminders { get; set; } = DefaultReminders.CreateSettings();
        public List<ReportTemplate> ReportTemplates { get; set; } = CreateDefaultReportTemplates();
        public EvidenceCaptureSettings EvidenceCapture { get; set; } = new();
        public DailyReportAutomationSettings DailyReportAutomation { get; set; } = new();
    }

    private sealed class PersistedModelProviderSettings
    {
        public string BaseUrl { get; set; } = "";
        public string ProtectedApiKey { get; set; } = "";
        public string Model { get; set; } = "";
        public string TextModel { get; set; } = "";
        public string VisionModel { get; set; } = "";
        public bool UseVision { get; set; } = true;
        public ModelQualityMode QualityMode { get; set; } = ModelQualityMode.Balanced;
        public PersistedSingleModelProviderSettings Analysis { get; set; } = new();
        public PersistedSingleModelProviderSettings ImageGeneration { get; set; } = new();
        public PersistedSingleModelProviderSettings Text { get; set; } = new();
        public PersistedSingleModelProviderSettings Vision { get; set; } = new();
    }

    private sealed class PersistedSingleModelProviderSettings
    {
        public string BaseUrl { get; set; } = "";
        public string ProtectedApiKey { get; set; } = "";
        public string Model { get; set; } = "";
    }
}
