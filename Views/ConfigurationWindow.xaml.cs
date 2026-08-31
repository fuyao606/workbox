using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Timenotifier.Models;
using Timenotifier.Services.Assistant;
using Timenotifier.Services.Automation;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Reporting;
using Timenotifier.Services.Settings;
using Timenotifier.Views.Assistant;

namespace Timenotifier.Views;

public enum ConfigurationSection
{
    Assistant,
    Reminders,
    Model,
    Evidence,
    Review,
    Reports,
    Capture,
    Tasks
}

internal enum ReportEditorEditSource
{
    Markdown,
    Structured
}

public partial class ConfigurationWindow : Window
{
    private readonly IAppSettingsStore _settingsStore;
    private readonly IEvidenceStore _evidenceStore;
    private readonly CompositeEvidenceCollector _evidenceCollector;
    private readonly ReportGenerationService _reportGeneration;
    private readonly DailyReportAutomationService _dailyReportAutomation;
    private readonly ITimeProvider _timeProvider;
    private readonly Func<Task> _reloadRemindersAsync;
    private readonly Func<Task> _reloadCaptureServicesAsync;
    private readonly Func<Task> _reloadDailyReportAutomationAsync;
    private readonly Func<Task<bool>> _runDailyReportAutomationAsync;
    private readonly Func<Task<bool>> _openDailyReportLoginBrowserAsync;
    private readonly EvidenceClusterer _clusterer = new();
    private readonly WeeklyReportExcelExporter _weeklyExcelExporter = new();
    private readonly ConfigurationViewModel _viewModel;
    private ReportDraft? _currentDraft;
    private bool _isLoadingReportEditors;
    private ReportEditorEditSource _lastReportEditorEditSource = ReportEditorEditSource.Structured;

    public ConfigurationWindow(
        IAppSettingsStore settingsStore,
        IEvidenceStore evidenceStore,
        CompositeEvidenceCollector evidenceCollector,
        ReportGenerationService reportGeneration,
        DailyReportAutomationService dailyReportAutomation,
        IAssistantConversationService assistantConversationService,
        IAssistantSessionStore assistantSessionStore,
        IAssistantAttachmentService assistantAttachmentService,
        ITimeProvider timeProvider,
        Func<Task> reloadRemindersAsync,
        Func<Task> reloadCaptureServicesAsync,
        Func<Task> reloadDailyReportAutomationAsync,
        Func<Task<bool>> runDailyReportAutomationAsync,
        Func<Task<bool>> openDailyReportLoginBrowserAsync,
        ConfigurationSection initialSection = ConfigurationSection.Assistant)
    {
        _settingsStore = settingsStore;
        _evidenceStore = evidenceStore;
        _evidenceCollector = evidenceCollector;
        _reportGeneration = reportGeneration;
        _dailyReportAutomation = dailyReportAutomation;
        _timeProvider = timeProvider;
        _reloadRemindersAsync = reloadRemindersAsync;
        _reloadCaptureServicesAsync = reloadCaptureServicesAsync;
        _reloadDailyReportAutomationAsync = reloadDailyReportAutomationAsync;
        _runDailyReportAutomationAsync = runDailyReportAutomationAsync;
        _openDailyReportLoginBrowserAsync = openDailyReportLoginBrowserAsync;
        _viewModel = new ConfigurationViewModel();

        InitializeComponent();
        AssistantHost.Content = new AssistantPage(
            assistantConversationService,
            assistantSessionStore,
            assistantAttachmentService,
            HandleAssistantExecutionResultAsync);
        DataContext = _viewModel;
        ScheduleKindColumn.ItemsSource = _viewModel.ScheduleKinds;
        HolidayKindColumn.ItemsSource = _viewModel.HolidayKinds;
        TemplateScopeColumn.ItemsSource = _viewModel.TemplateScopes;
        QualityModeBox.ItemsSource = _viewModel.QualityModes;
        ReportContentBox.TextChanged += OnReportMarkdownTextChanged;
        WorkSummaryBox.TextChanged += OnReportStructuredTextChanged;
        ProblemsBox.TextChanged += OnReportStructuredTextChanged;
        NextWeekPlanBox.TextChanged += OnReportStructuredTextChanged;
        SupportSuggestionsBox.TextChanged += OnReportStructuredTextChanged;
        SelectSection(initialSection);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeAsync();
        await WriteUiLoopEvidenceInspectionAsync();
    }

    private async Task WriteUiLoopEvidenceInspectionAsync()
    {
        var diagnosticsPath = GetUiLoopArgumentValue("--ui-loop-evidence-inspection-path");
        var renderPath = GetUiLoopArgumentValue("--ui-loop-render-path");
        if (string.IsNullOrWhiteSpace(diagnosticsPath) && string.IsNullOrWhiteSpace(renderPath))
        {
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                EvidenceGrid.UpdateLayout();

                if (!string.IsNullOrWhiteSpace(diagnosticsPath))
                {
                    var payload = new
                    {
                        EvidenceGrid.Items.Count,
                        EvidenceGrid.AutoGenerateColumns,
                        HeadersVisibility = EvidenceGrid.HeadersVisibility.ToString(),
                        ActualWidth = FormatUiLoopNumber(EvidenceGrid.ActualWidth),
                        ActualHeight = FormatUiLoopNumber(EvidenceGrid.ActualHeight),
                        MinWidth = FormatUiLoopNumber(EvidenceGrid.MinWidth),
                        MaxWidth = FormatUiLoopNumber(EvidenceGrid.MaxWidth),
                        MinColumnWidth = FormatUiLoopNumber(EvidenceGrid.MinColumnWidth),
                        MaxColumnWidth = FormatUiLoopNumber(EvidenceGrid.MaxColumnWidth),
                        ColumnWidth = EvidenceGrid.ColumnWidth.ToString(),
                        ColumnHeaderHeight = FormatUiLoopNumber(EvidenceGrid.ColumnHeaderHeight),
                        RowHeaderWidth = FormatUiLoopNumber(EvidenceGrid.RowHeaderWidth),
                        Columns = EvidenceGrid.Columns.Select(column => new
                        {
                            Header = column.Header?.ToString(),
                            column.DisplayIndex,
                            Width = column.Width.ToString(),
                            column.ActualWidth,
                            Visibility = column.Visibility.ToString()
                        })
                    };
                    var directory = Path.GetDirectoryName(diagnosticsPath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(diagnosticsPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
                }

                if (!string.IsNullOrWhiteSpace(renderPath))
                {
                    var directory = Path.GetDirectoryName(renderPath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var dpi = VisualTreeHelper.GetDpi(this);
                    var bitmap = new RenderTargetBitmap(
                        Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
                        Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)),
                        dpi.PixelsPerInchX,
                        dpi.PixelsPerInchY,
                        PixelFormats.Pbgra32);
                    bitmap.Render(this);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(renderPath);
                    encoder.Save(stream);
                }
            }, DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(diagnosticsPath))
            {
                var directory = Path.GetDirectoryName(diagnosticsPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(diagnosticsPath, JsonSerializer.Serialize(new { Error = ex.Message }));
            }
        }
    }

    private static string? GetUiLoopArgumentValue(string name)
    {
        return Environment.GetCommandLineArgs()
            .FirstOrDefault(argument => argument.StartsWith($"{name}=", StringComparison.OrdinalIgnoreCase))?
            [(name.Length + 1)..];
    }

    private static string FormatUiLoopNumber(double value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    public void SelectSection(ConfigurationSection section)
    {
        if (Sections is null)
        {
            return;
        }

        Sections.SelectedIndex = GetSectionIndex(section);

        if (CurrentSectionTitleText is not null)
        {
            CurrentSectionTitleText.Text = section switch
            {
                ConfigurationSection.Reminders => "提醒",
                ConfigurationSection.Model => "模型",
                ConfigurationSection.Evidence => "证据",
                ConfigurationSection.Review => "回顾",
                ConfigurationSection.Reports => "报告",
                ConfigurationSection.Capture => "采集",
                ConfigurationSection.Tasks => "任务",
                _ => "AI 助手"
            };
        }

        SynchronizeNavigationSelection(NavigationList, section);
        SynchronizeNavigationSelection(SystemNavigationList, section);

        if (ToolsMenuPopup is not null)
        {
            ToolsMenuPopup.IsOpen = false;
        }
    }

    private static void SynchronizeNavigationSelection(System.Windows.Controls.ListBox? navigationList, ConfigurationSection section)
    {
        if (navigationList is null)
        {
            return;
        }

        var navigationItem = navigationList.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), section.ToString(), StringComparison.OrdinalIgnoreCase));
        if (navigationItem is not null && !navigationItem.IsSelected)
        {
            navigationList.SelectedItem = navigationItem;
        }
        else if (navigationItem is null)
        {
            navigationList.SelectedItem = null;
        }
    }

    private void OnNavigationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox { SelectedItem: ListBoxItem { Tag: string sectionName } }
            || !Enum.TryParse<ConfigurationSection>(sectionName, out var section))
        {
            return;
        }

        if (Sections.SelectedIndex != GetSectionIndex(section))
        {
            SelectSection(section);
        }
    }

    private static int GetSectionIndex(ConfigurationSection section)
    {
        return section switch
        {
            ConfigurationSection.Reminders => 1,
            ConfigurationSection.Model => 2,
            ConfigurationSection.Evidence => 3,
            ConfigurationSection.Review => 4,
            ConfigurationSection.Reports => 5,
            ConfigurationSection.Capture => 6,
            ConfigurationSection.Tasks => 7,
            _ => 0
        };
    }

    private void OnAssistantHomeClicked(object sender, RoutedEventArgs e)
    {
        SelectSection(ConfigurationSection.Assistant);
    }

    private void OnToolsMenuClicked(object sender, RoutedEventArgs e)
    {
        ToolsMenuPopup.IsOpen = !ToolsMenuPopup.IsOpen;
    }

    private void OnToolMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string sectionName
            || !Enum.TryParse<ConfigurationSection>(sectionName, out var section))
        {
            return;
        }

        SelectSection(section);
        ToolsMenuButton.Focus();
    }

    private async Task HandleAssistantExecutionResultAsync(AssistantActionExecutionResult result)
    {
        await RefreshAssistantTargetsAsync(result.RefreshTargets);
        if (result.HostCommand is null)
        {
            SetStatus(result.Message);
            return;
        }

        var command = result.HostCommand;
        switch (command.Kind)
        {
            case AssistantHostCommandKind.NavigateModule:
                SelectSection(ToConfigurationSection(command.Module ?? AssistantModule.Assistant));
                break;
            case AssistantHostCommandKind.PreviewEvidenceImage:
                await PreviewEvidenceImageAsync(command.TargetId);
                break;
            case AssistantHostCommandKind.LoadReportDraft:
                await LoadReportDraftAsync(command.TargetId);
                SelectSection(ConfigurationSection.Reports);
                break;
            case AssistantHostCommandKind.SaveCurrentReport:
                await SaveCurrentDraftAsync(ReportStatuses.Draft);
                SelectSection(ConfigurationSection.Reports);
                break;
            case AssistantHostCommandKind.ConfirmCurrentReport:
                await SaveCurrentDraftAsync(ReportStatuses.Confirmed);
                SelectSection(ConfigurationSection.Reports);
                break;
            case AssistantHostCommandKind.NewDraftFromConfirmed:
                await NewDraftFromConfirmedAsync(command.TargetId);
                SelectSection(ConfigurationSection.Reports);
                break;
            case AssistantHostCommandKind.CopyCurrentReport:
                CopyCurrentReport(command.Value);
                break;
            case AssistantHostCommandKind.ExportWeeklyReport:
                await ExportWeeklyExcelAsync(string.Equals(command.Value, "school", StringComparison.OrdinalIgnoreCase));
                SelectSection(ConfigurationSection.Reports);
                break;
            case AssistantHostCommandKind.OpenAutomationLogin:
                await _openDailyReportLoginBrowserAsync();
                break;
            case AssistantHostCommandKind.RunDailyAutomation:
                await _runDailyReportAutomationAsync();
                break;
        }
    }

    private async Task RefreshAssistantTargetsAsync(AssistantRefreshTargets targets)
    {
        if (targets.HasFlag(AssistantRefreshTargets.Reminders))
        {
            await LoadReminderSettingsAsync();
            await _reloadRemindersAsync();
        }
        if (targets.HasFlag(AssistantRefreshTargets.Model))
        {
            await LoadModelSettingsAsync();
        }
        if (targets.HasFlag(AssistantRefreshTargets.Evidence))
        {
            await LoadStoredEvidenceAsync();
        }
        if (targets.HasFlag(AssistantRefreshTargets.Review))
        {
            await LoadTimelineReviewAsync();
        }
        if (targets.HasFlag(AssistantRefreshTargets.Reports))
        {
            await RefreshReportHistoryAsync();
        }
        if (targets.HasFlag(AssistantRefreshTargets.Capture))
        {
            await LoadCaptureSettingsAsync();
            await _reloadCaptureServicesAsync();
        }
        if (targets.HasFlag(AssistantRefreshTargets.Automation))
        {
            await LoadDailyReportAutomationSettingsAsync();
            await _reloadDailyReportAutomationAsync();
        }
    }

    private static ConfigurationSection ToConfigurationSection(AssistantModule module)
    {
        return module switch
        {
            AssistantModule.Reminders => ConfigurationSection.Reminders,
            AssistantModule.Model => ConfigurationSection.Model,
            AssistantModule.Evidence => ConfigurationSection.Evidence,
            AssistantModule.Review => ConfigurationSection.Review,
            AssistantModule.Reports => ConfigurationSection.Reports,
            AssistantModule.Capture => ConfigurationSection.Capture,
            AssistantModule.Tasks => ConfigurationSection.Tasks,
            _ => ConfigurationSection.Assistant
        };
    }

    private async Task InitializeAsync()
    {
        await LoadReminderSettingsAsync();
        await LoadCaptureSettingsAsync();
        await LoadReportTemplatesAsync();
        await RefreshReportHistoryAsync();
        await LoadModelSettingsAsync();
        await LoadStoredEvidenceAsync();
        await LoadTimelineReviewAsync();
        await LoadDailyReportAutomationSettingsAsync();
    }

    private async Task LoadReminderSettingsAsync()
    {
        var settings = await _settingsStore.LoadReminderSettingsAsync();
        SnoozeMinutesBox.Text = settings.SnoozeMinutes.ToString();
        _viewModel.ReplaceReminders(settings.Rules.Select(ReminderRuleEditor.FromRule));
        _viewModel.ReplaceHolidayAdjustments(settings.HolidayAdjustments.Select(HolidayAdjustmentEditor.FromAdjustment));
        ReminderGrid.Items.Refresh();
        HolidayGrid.Items.Refresh();
    }

    private async Task LoadModelSettingsAsync()
    {
        var settings = await _settingsStore.LoadModelSettingsAsync();
        TextBaseUrlBox.Text = settings.Analysis.BaseUrl;
        TextApiKeyBox.Password = settings.Analysis.ApiKey;
        TextModelBox.Text = settings.Analysis.Model;
        VisionBaseUrlBox.Text = settings.ImageGeneration.BaseUrl;
        VisionApiKeyBox.Password = settings.ImageGeneration.ApiKey;
        VisionModelBox.Text = settings.ImageGeneration.Model;
        UseVisionBox.IsChecked = settings.UseVision;
        QualityModeBox.SelectedItem = settings.QualityMode;
    }

    private async Task LoadCaptureSettingsAsync()
    {
        var settings = await _settingsStore.LoadEvidenceCaptureSettingsAsync();
        AutoScreenshotEnabledBox.IsChecked = settings.AutoScreenshotEnabled;
        ScreenshotIntervalBox.Text = settings.ScreenshotIntervalMinutes.ToString();
        ScreenshotDirectoryBox.Text = settings.ScreenshotDirectory;
        MaxAutoScreenshotsBox.Text = settings.MaxAutoScreenshots.ToString();
        ManualHotkeyBox.Text = settings.ManualNoteHotkey;
        StartWithWindowsBox.IsChecked = settings.StartWithWindows;
        OcrEnabledBox.IsChecked = settings.OcrEnabled;
        PrivacyFiltersBox.Text = settings.DisplayPrivacyFilters;
        DefaultTagsBox.Text = settings.DisplayDefaultTags;
    }

    private async Task LoadDailyReportAutomationSettingsAsync()
    {
        var settings = await _settingsStore.LoadDailyReportAutomationSettingsAsync();
        DailyAutomationEnabledBox.IsChecked = settings.IsEnabled;
        DailyAutomationRunTimeBox.Text = settings.RunTime.ToString("HH:mm");
        DailyAutomationUrlBox.Text = settings.TargetUrl;
        DailyAutomationSessionBox.Text = settings.BrowserSessionName;
        DailyAutomationProfileBox.Text = settings.BrowserProfileDirectory;
        DailyAutomationProjectBox.Text = settings.ProjectName;
        DailyAutomationTaskBox.Text = settings.TaskName;
        DailyAutomationTypeBox.Text = settings.TaskType;
        DailyAutomationWorkloadBox.Text = settings.WorkloadPercent.ToString();
    }

    private async Task LoadReportTemplatesAsync()
    {
        var templates = await _settingsStore.LoadReportTemplatesAsync();
        _viewModel.ReplaceReportTemplates(templates.Select(ReportTemplateEditor.FromTemplate));
        ReportTemplateBox.ItemsSource = _viewModel.ReportTemplates;
        ReportTemplateBox.DisplayMemberPath = nameof(ReportTemplateEditor.Name);
        ReportTemplateBox.SelectedItem = _viewModel.ReportTemplates.FirstOrDefault(template =>
            string.Equals(template.Id, "built-in-dev", StringComparison.OrdinalIgnoreCase)
            || template.Name.Contains("研发", StringComparison.OrdinalIgnoreCase)
            || template.Name.Contains("开发", StringComparison.OrdinalIgnoreCase));
        if (ReportTemplateBox.SelectedItem is null)
        {
            ReportTemplateBox.SelectedIndex = _viewModel.ReportTemplates.Count > 0 ? 0 : -1;
        }

        ReportTemplateGrid.Items.Refresh();
    }

    private async Task RefreshReportHistoryAsync()
    {
        var drafts = await _evidenceStore.ListReportDraftsAsync();
        _viewModel.ReplaceReportDrafts(drafts);
        ReportHistoryGrid.Items.Refresh();
    }

    private async Task RefreshEvidenceAsync()
    {
        SetStatus("正在获取证据...");
        EvidenceGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        EvidenceGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var (startDate, endDate) = GetSelectedEvidenceRange();
        var evidence = await _evidenceCollector.CollectAndStoreAsync(new EvidenceRange(startDate, endDate));
        _viewModel.ReplaceEvidence(evidence);
        EvidenceGrid.Items.Refresh();

        var latestCodex = evidence
            .Where(item => EvidenceKinds.IsAiSession(item.Kind))
            .OrderByDescending(item => item.CapturedAt)
            .FirstOrDefault();
        var latestText = latestCodex is null
            ? ""
            : $" 最新 AI 会话：{latestCodex.CapturedAt:HH:mm:ss} {latestCodex.DisplayKind} {latestCodex.Title}";
        SetStatus($"已获取 {DescribeRange(startDate, endDate)}证据：{evidence.Count} 条，时间：{_timeProvider.Now:HH:mm:ss}。{latestText}");
    }

    private async Task LoadStoredEvidenceAsync()
    {
        var (startDate, endDate) = GetSelectedEvidenceRange();
        var evidence = await _evidenceStore.ReadEvidenceAsync(startDate, endDate);
        _viewModel.ReplaceEvidence(evidence);
        EvidenceGrid.Items.Refresh();
        SetStatus($"已载入{DescribeRange(startDate, endDate)}已存证据：{evidence.Count} 条。");
    }

    private async Task LoadTimelineReviewAsync()
    {
        var (startDate, endDate) = GetSelectedTimelineRange();
        var evidence = (await _evidenceStore.ReadEvidenceAsync(startDate, endDate))
            .OrderBy(item => item.CapturedAt)
            .ToList();
        _viewModel.ReplaceTimelineGroups(BuildTimelineGroups(evidence, startDate, endDate));
        RefreshTimelineStats(evidence);
        SetStatus($"已加载{DescribeRange(startDate, endDate)}回顾：{evidence.Count} 条。");
    }

    private (DateOnly StartDate, DateOnly EndDate) GetSelectedTimelineRange()
    {
        var today = DateOnly.FromDateTime(_timeProvider.Now);
        return TimelineRangeBox.SelectedIndex == 1
            ? GetCurrentInternshipWorkWeek(today)
            : (today, today);
    }

    private static IEnumerable<TimelineGroup> BuildTimelineGroups(
        IReadOnlyList<EvidenceItem> evidence,
        DateOnly startDate,
        DateOnly endDate)
    {
        if (startDate == endDate)
        {
            return evidence
                .GroupBy(item => item.CapturedAt.Hour)
                .OrderBy(group => group.Key)
                .Select(group => new TimelineGroup($"{group.Key:00}:00", group));
        }

        return evidence
            .GroupBy(item => DateOnly.FromDateTime(item.CapturedAt))
            .OrderBy(group => group.Key)
            .Select(group => new TimelineGroup($"{group.Key:MM-dd dddd}", group));
    }

    private void RefreshTimelineStats(IReadOnlyList<EvidenceItem> evidence)
    {
        TimelineTotalText.Text = evidence.Count.ToString();
        TimelineIncludedText.Text = evidence.Count(item => item.Included).ToString();
        TimelineScreenshotText.Text = evidence.Count(item => item.Kind == EvidenceKinds.Screenshot).ToString();
        TimelineManualText.Text = evidence.Count(item => item.Kind == EvidenceKinds.ManualNote).ToString();
        TimelineOcrText.Text = evidence.Count(item => !string.IsNullOrWhiteSpace(item.OcrText)).ToString();
        TimelineWindowText.Text = evidence
            .Where(item => !string.IsNullOrWhiteSpace(item.WindowTitle) || !string.IsNullOrWhiteSpace(item.ProcessName))
            .Select(item => $"{item.ProcessName}|{item.WindowTitle}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ToString();
    }

    private async Task GenerateReportAsync(string reportType)
    {
        try
        {
            SetStatus("正在生成报告草稿...");
            var today = DateOnly.FromDateTime(_timeProvider.Now);
            var (startDate, endDate) = reportType == ReportTypes.Weekly
                ? GetCurrentInternshipWorkWeek(today)
                : (today, today);

            var evidence = (await _evidenceStore.ReadEvidenceAsync(startDate, endDate))
                .Where(item => item.Included)
                .ToList();
            _viewModel.ReplaceReportEvidence(evidence);
            ReportEvidenceGrid.Items.Refresh();
            var template = (ReportTemplateBox.SelectedItem as ReportTemplateEditor)?.ToTemplate();
            var result = await _reportGeneration.GenerateFromEvidenceAsync(reportType, startDate, endDate, _viewModel.ReportEvidence.ToList(), template);
            _currentDraft = result.Draft;
            LoadDraftIntoEditors(result.Draft);
            SetReportEditorsReadOnly(false);
            _viewModel.ReplaceReportClusters(result.Clusters);
            _viewModel.ReplaceReportEvidence(result.Evidence);
            ReportEvidenceGrid.Items.Refresh();
            ReportClusterGrid.Items.Refresh();
            await RefreshReportHistoryAsync();
            SetStatus($"已生成{(reportType == ReportTypes.Weekly ? "周报" : "日报")}草稿。");
        }
        catch (Exception ex)
        {
            SetStatus($"生成失败：{ResponsesModelClient.FlattenException(ex)}");
        }
    }

    private static (DateOnly StartDate, DateOnly EndDate) GetCurrentInternshipWorkWeek(DateOnly today)
    {
        var week = InternshipWeekCalculator.GetWeek(today);
        return (week.StartDate, week.EndDate);
    }

    private (DateOnly StartDate, DateOnly EndDate) GetSelectedEvidenceRange()
    {
        var today = DateOnly.FromDateTime(_timeProvider.Now);
        return EvidenceRangeBox.SelectedIndex == 1
            ? GetCurrentInternshipWorkWeek(today)
            : (today, today);
    }

    private static string DescribeRange(DateOnly startDate, DateOnly endDate)
    {
        return startDate == endDate ? "今日" : "本周";
    }

    private async void OnSaveModelClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await _settingsStore.SaveModelSettingsAsync(new ModelProviderSettings
            {
                Analysis = new SingleModelProviderSettings
                {
                    BaseUrl = TextBaseUrlBox.Text,
                    ApiKey = TextApiKeyBox.Password,
                    Model = TextModelBox.Text
                },
                ImageGeneration = new SingleModelProviderSettings
                {
                    BaseUrl = VisionBaseUrlBox.Text,
                    ApiKey = VisionApiKeyBox.Password,
                    Model = VisionModelBox.Text
                },
                UseVision = UseVisionBox.IsChecked == true,
                QualityMode = QualityModeBox.SelectedItem is ModelQualityMode mode
                    ? mode
                    : ModelQualityMode.Balanced
            });
            SetStatus("模型配置已保存。");
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败：{ex.Message}");
        }
    }

    private async void OnTestAnalysisClicked(object sender, RoutedEventArgs e)
    {
        await TestModelConnectionAsync(
            new SingleModelProviderSettings
            {
                BaseUrl = TextBaseUrlBox.Text,
                ApiKey = TextApiKeyBox.Password,
                Model = TextModelBox.Text
            },
            ModelConnectionTestKind.Analysis);
    }

    private async void OnTestImageGenerationClicked(object sender, RoutedEventArgs e)
    {
        await TestModelConnectionAsync(
            new SingleModelProviderSettings
            {
                BaseUrl = VisionBaseUrlBox.Text,
                ApiKey = VisionApiKeyBox.Password,
                Model = VisionModelBox.Text
            },
            ModelConnectionTestKind.ImageGeneration);
    }

    private async Task TestModelConnectionAsync(SingleModelProviderSettings provider, ModelConnectionTestKind kind)
    {
        SetStatus($"正在测试 {(kind == ModelConnectionTestKind.Analysis ? "Analysis" : "ImageGeneration")}...");
        var result = await new ResponsesModelClient().TestConnectionAsync(provider, kind);
        var fallback = result.FallbackUsed ? "，已使用兼容兜底" : "";
        var endpoint = string.IsNullOrWhiteSpace(result.EndpointUsed) ? "" : $" 地址：{result.EndpointUsed}";
        SetStatus($"{result.Message}{fallback}{endpoint}");
    }

    private async void OnSaveCaptureSettingsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(ScreenshotIntervalBox.Text.Trim(), out var interval))
            {
                SetStatus("截图间隔必须是数字。");
                return;
            }

            if (!int.TryParse(MaxAutoScreenshotsBox.Text.Trim(), out var maxCount))
            {
                SetStatus("最多保留数量必须是数字。");
                return;
            }

            var settings = new EvidenceCaptureSettings
            {
                AutoScreenshotEnabled = AutoScreenshotEnabledBox.IsChecked == true,
                ScreenshotIntervalMinutes = interval,
                ScreenshotDirectory = ScreenshotDirectoryBox.Text,
                MaxAutoScreenshots = maxCount,
                ManualNoteHotkey = ManualHotkeyBox.Text,
                StartWithWindows = StartWithWindowsBox.IsChecked == true,
                OcrEnabled = OcrEnabledBox.IsChecked == true
            };
            settings.DisplayPrivacyFilters = PrivacyFiltersBox.Text;
            settings.DisplayDefaultTags = DefaultTagsBox.Text;

            await _settingsStore.SaveEvidenceCaptureSettingsAsync(settings);
            new StartupRegistrationService().SetEnabled(settings.StartWithWindows);
            await _reloadCaptureServicesAsync();
            await LoadCaptureSettingsAsync();
            SetStatus("采集设置已保存并生效。");
        }
        catch (Exception ex)
        {
            SetStatus($"保存采集设置失败：{ex.Message}");
        }
    }

    private async void OnSaveDailyAutomationSettingsClicked(object sender, RoutedEventArgs e)
    {
        await SaveDailyAutomationSettingsAsync();
    }

    private async void OnRunDailyAutomationNowClicked(object sender, RoutedEventArgs e)
    {
        if (!await SaveDailyAutomationSettingsAsync())
        {
            return;
        }

        SetStatus("正在启动日报自动化试运行...");
        if (await _runDailyReportAutomationAsync())
        {
            SetStatus("日报自动化已启动，请在确认窗口中检查填报内容。");
        }
        else
        {
            SetStatus("日报自动化启动失败，请查看弹窗提示。");
        }
    }

    private async void OnOpenDailyAutomationLoginBrowserClicked(object sender, RoutedEventArgs e)
    {
        if (!await SaveDailyAutomationSettingsAsync())
        {
            return;
        }

        SetStatus("正在打开日报系统登录浏览器...");
        if (await _openDailyReportLoginBrowserAsync())
        {
            SetStatus("已打开登录浏览器。");
        }
        else
        {
            SetStatus("打开登录浏览器失败，请查看弹窗提示。");
        }
    }

    private void OnBrowseDailyAutomationProfileClicked(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择日报自动化浏览器配置目录",
            UseDescriptionForTitle = true,
            SelectedPath = DailyAutomationProfileBox.Text
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            DailyAutomationProfileBox.Text = dialog.SelectedPath;
        }
    }

    private async Task<bool> SaveDailyAutomationSettingsAsync()
    {
        try
        {
            if (!TimeOnly.TryParse(DailyAutomationRunTimeBox.Text.Trim(), out var runTime))
            {
                SetStatus("任务执行时间必须是 HH:mm 格式。");
                return false;
            }

            if (!int.TryParse(DailyAutomationWorkloadBox.Text.Trim(), out var workload))
            {
                SetStatus("工作量占比必须是数字。");
                return false;
            }

            var settings = new DailyReportAutomationSettings
            {
                IsEnabled = DailyAutomationEnabledBox.IsChecked == true,
                RunTime = runTime,
                TargetUrl = DailyAutomationUrlBox.Text,
                BrowserSessionName = DailyAutomationSessionBox.Text,
                BrowserProfileDirectory = DailyAutomationProfileBox.Text,
                ProjectName = DailyAutomationProjectBox.Text,
                TaskName = DailyAutomationTaskBox.Text,
                TaskType = DailyAutomationTypeBox.Text,
                WorkloadPercent = workload
            };

            await _settingsStore.SaveDailyReportAutomationSettingsAsync(settings);
            await _reloadDailyReportAutomationAsync();
            await LoadDailyReportAutomationSettingsAsync();
            SetStatus("任务配置已保存并生效。");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"保存任务配置失败：{ex.Message}");
            return false;
        }
    }

    private void OnAddReminderClicked(object sender, RoutedEventArgs e)
    {
        var reminder = new ReminderRuleEditor
        {
            Message = "时间提醒",
            TimeText = _timeProvider.Now.AddMinutes(10).ToString("HH:mm"),
            ScheduleKind = ReminderScheduleKind.Workdays,
            IsEnabled = true
        };
        _viewModel.Reminders.Add(reminder);
        ReminderGrid.SelectedItem = reminder;
        ReminderGrid.ScrollIntoView(reminder);
        ReminderGrid.Focus();
        SetStatus("已新增提醒，保存后生效。");
    }

    private void OnDeleteReminderClicked(object sender, RoutedEventArgs e)
    {
        if (ReminderGrid.SelectedItem is not ReminderRuleEditor selected)
        {
            SetStatus("请先选择要删除的提醒。");
            return;
        }

        _viewModel.Reminders.Remove(selected);
        SetStatus("已删除提醒，保存后生效。");
    }

    private void OnAddHolidayClicked(object sender, RoutedEventArgs e)
    {
        var adjustment = new HolidayAdjustmentEditor
        {
            DateText = DateOnly.FromDateTime(_timeProvider.Now).ToString("yyyy-MM-dd"),
            Kind = HolidayAdjustmentKind.Holiday,
            Name = "节假日"
        };
        _viewModel.HolidayAdjustments.Add(adjustment);
        HolidayGrid.SelectedItem = adjustment;
        HolidayGrid.ScrollIntoView(adjustment);
        HolidayGrid.Focus();
        SetStatus("已新增日期规则，保存后生效。");
    }

    private void OnDeleteHolidayClicked(object sender, RoutedEventArgs e)
    {
        if (HolidayGrid.SelectedItem is not HolidayAdjustmentEditor selected)
        {
            SetStatus("请先选择要删除的日期规则。");
            return;
        }

        _viewModel.HolidayAdjustments.Remove(selected);
        SetStatus("已删除日期规则，保存后生效。");
    }

    private async void OnSaveRemindersClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ReminderGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ReminderGrid.CommitEdit(DataGridEditingUnit.Row, true);
            HolidayGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            HolidayGrid.CommitEdit(DataGridEditingUnit.Row, true);

            if (!int.TryParse(SnoozeMinutesBox.Text.Trim(), out var snoozeMinutes))
            {
                SetStatus("稍后提醒分钟数必须是数字。");
                return;
            }

            var settings = new ReminderSettings
            {
                SnoozeMinutes = snoozeMinutes,
                Rules = _viewModel.Reminders.Select(editor => editor.ToRule()).ToList(),
                HolidayAdjustments = _viewModel.HolidayAdjustments.Select(editor => editor.ToAdjustment()).ToList()
            };

            await _settingsStore.SaveReminderSettingsAsync(settings);
            await _reloadRemindersAsync();
            await LoadReminderSettingsAsync();
            SetStatus("提醒配置已保存并生效。");
        }
        catch (Exception ex)
        {
            SetStatus($"保存提醒失败：{ex.Message}");
        }
    }

    private async void OnRefreshEvidenceClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveEvidenceEditsAsync();
            await RefreshEvidenceAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"获取证据失败：{ResponsesModelClient.FlattenException(ex)}");
        }
    }

    private async void OnEvidenceIncludedClicked(object sender, RoutedEventArgs e)
    {
        EvidenceGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        EvidenceGrid.CommitEdit(DataGridEditingUnit.Row, true);
        await SaveEvidenceEditsAsync();
        SetStatus("证据纳入状态已保存。");
    }

    private async void OnRefreshTimelineClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadTimelineReviewAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"加载回顾失败：{ResponsesModelClient.FlattenException(ex)}");
        }
    }

    private async void OnTimelineRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || TimelineRangeBox is null)
        {
            return;
        }

        await LoadTimelineReviewAsync();
    }

    private async void OnTimelineIncludedClicked(object sender, RoutedEventArgs e)
    {
        var items = _viewModel.TimelineGroups
            .SelectMany(group => group.Items)
            .Select(entry => entry.Evidence)
            .ToList();
        await _evidenceStore.UpsertEvidenceAsync(items);
        SyncIncludedStates(items);
        EvidenceGrid.Items.Refresh();
        ReportEvidenceGrid.Items.Refresh();
        SetStatus("回顾纳入状态已保存。");
    }

    private void OnOpenEvidenceManagementClicked(object sender, RoutedEventArgs e)
    {
        SelectSection(ConfigurationSection.Evidence);
    }

    private void SyncIncludedStates(IEnumerable<EvidenceItem> sourceItems)
    {
        var includedById = sourceItems.ToDictionary(item => item.Id, item => item.Included, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _viewModel.Evidence.Concat(_viewModel.ReportEvidence))
        {
            if (includedById.TryGetValue(item.Id, out var included))
            {
                item.Included = included;
            }
        }
    }

    private async void OnDeleteEvidenceClicked(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedEvidenceAsync();
    }

    private async void OnEvidenceGridKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete)
        {
            return;
        }

        e.Handled = true;
        await DeleteSelectedEvidenceAsync();
    }

    private async Task DeleteSelectedEvidenceAsync()
    {
        var selected = EvidenceGrid.SelectedItems.OfType<EvidenceItem>().DistinctBy(item => item.Id).ToList();
        if (selected.Count == 0)
        {
            SetStatus("请先选择要删除的证据。");
            return;
        }

        var result = System.Windows.MessageBox.Show(
            this,
            $"确定删除选中的 {selected.Count} 条证据吗？截图证据会同时删除关联图片。",
            "批量删除证据",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var selectedIds = selected.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletedCount = await _evidenceStore.DeleteEvidenceAsync(selectedIds);
        foreach (var item in selected)
        {
            _viewModel.Evidence.Remove(item);
        }

        foreach (var reportEvidence in _viewModel.ReportEvidence.Where(reportEvidence => selectedIds.Contains(reportEvidence.Id)).ToList())
        {
            _viewModel.ReportEvidence.Remove(reportEvidence);
        }

        EvidenceGrid.Items.Refresh();
        ReportEvidenceGrid.Items.Refresh();
        SetStatus(deletedCount > 0 ? $"已删除 {deletedCount} 条证据。" : "未找到要删除的证据。");
    }

    private void OnBrowseScreenshotDirectoryClicked(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择自动截图保存位置",
            UseDescriptionForTitle = true,
            SelectedPath = ScreenshotDirectoryBox.Text
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            ScreenshotDirectoryBox.Text = dialog.SelectedPath;
        }
    }

    private async void OnRefreshReportEvidenceClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var today = DateOnly.FromDateTime(_timeProvider.Now);
            var reportType = ReportTypeBox.SelectedIndex == 1 ? ReportTypes.Weekly : ReportTypes.Daily;
            var (startDate, endDate) = reportType == ReportTypes.Weekly
                ? GetCurrentInternshipWorkWeek(today)
                : (today, today);
            await SaveEvidenceEditsAsync();
            var evidence = (await _evidenceStore.ReadEvidenceAsync(startDate, endDate))
                .Where(item => item.Included)
                .ToList();
            _viewModel.ReplaceReportEvidence(evidence);
            _viewModel.ReplaceReportClusters(_clusterer.Cluster(evidence));
            ReportEvidenceGrid.Items.Refresh();
            ReportClusterGrid.Items.Refresh();
            SetStatus($"已获取{(reportType == ReportTypes.Weekly ? "本周" : "今日")}已选证据：{evidence.Count} 条。");
        }
        catch (Exception ex)
        {
            SetStatus($"刷新报告证据失败：{ex.Message}");
        }
    }

    private void OnPreviewEvidenceImageClicked(object sender, RoutedEventArgs e)
    {
        var evidence = (sender as FrameworkElement)?.DataContext switch
        {
            EvidenceItem item => item,
            TimelineEntry entry => entry.Evidence,
            _ => null
        };
        if (evidence is null)
        {
            return;
        }

        ShowEvidencePreview(evidence);
    }

    private async Task PreviewEvidenceImageAsync(string? evidenceId)
    {
        if (string.IsNullOrWhiteSpace(evidenceId))
        {
            throw new InvalidOperationException("证据 ID 不能为空。");
        }

        var today = DateOnly.FromDateTime(_timeProvider.Now);
        var evidence = (await _evidenceStore.ReadEvidenceAsync(today.AddYears(-1), today))
            .FirstOrDefault(item => string.Equals(item.Id, evidenceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("证据已不存在，请刷新后重试。");
        ShowEvidencePreview(evidence);
    }

    private void ShowEvidencePreview(EvidenceItem evidence)
    {
        if (!evidence.CanPreviewImage || string.IsNullOrWhiteSpace(evidence.ImagePath))
        {
            SetStatus("当前证据没有可预览的截图文件。");
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(evidence.ImagePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            var viewer = new Window
            {
                Title = $"截图预览 - {evidence.CapturedAt:yyyy-MM-dd HH:mm:ss}",
                Owner = this,
                Width = 980,
                Height = 720,
                MinWidth = 640,
                MinHeight = 420,
                Background = System.Windows.Media.Brushes.Black,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new System.Windows.Controls.Image
                    {
                        Source = image,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(12)
                    }
                }
            };
            viewer.Show();
            SetStatus($"已打开截图预览：{Path.GetFileName(evidence.ImagePath)}");
        }
        catch (Exception ex)
        {
            SetStatus($"截图预览失败：{ex.Message}");
        }
    }

    private async void OnGenerateDailyClicked(object sender, RoutedEventArgs e)
    {
        await SaveEvidenceEditsAsync();
        await GenerateReportAsync(ReportTypes.Daily);
    }

    private async void OnGenerateWeeklyClicked(object sender, RoutedEventArgs e)
    {
        await SaveEvidenceEditsAsync();
        await GenerateReportAsync(ReportTypes.Weekly);
    }

    private void OnCopyReportClicked(object sender, RoutedEventArgs e)
    {
        OnCopyMarkdownClicked(sender, e);
    }

    private void OnCopyMarkdownClicked(object sender, RoutedEventArgs e)
    {
        SyncCurrentDraftFromEditors();
        if (!string.IsNullOrWhiteSpace(ReportContentBox.Text))
        {
            System.Windows.Clipboard.SetText(ReportContentBox.Text);
            SetStatus("Markdown 已复制。");
        }
    }

    private void OnCopyPlainTextClicked(object sender, RoutedEventArgs e)
    {
        SyncCurrentDraftFromEditors();
        if (!string.IsNullOrWhiteSpace(ReportContentBox.Text))
        {
            System.Windows.Clipboard.SetText(ReportTextFormatter.ToPlainText(ReportContentBox.Text));
            SetStatus("纯文本已复制。");
        }
    }

    private void CopyCurrentReport(string? format)
    {
        SyncCurrentDraftFromEditors();
        if (string.IsNullOrWhiteSpace(ReportContentBox.Text))
        {
            SetStatus("当前没有可复制的报告内容。");
            return;
        }

        var markdown = string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase);
        System.Windows.Clipboard.SetText(markdown
            ? ReportContentBox.Text
            : ReportTextFormatter.ToPlainText(ReportContentBox.Text));
        SetStatus(markdown ? "Markdown 已复制。" : "纯文本已复制。");
    }

    private async void OnExportEnterpriseWeeklyExcelClicked(object sender, RoutedEventArgs e)
    {
        await ExportWeeklyExcelAsync(exportSchoolTemplate: false);
    }

    private async void OnExportSchoolWeeklyExcelClicked(object sender, RoutedEventArgs e)
    {
        await ExportWeeklyExcelAsync(exportSchoolTemplate: true);
    }

    private async Task ExportWeeklyExcelAsync(bool exportSchoolTemplate)
    {
        try
        {
            if (_currentDraft is null)
            {
                SetStatus("请先生成或载入本周周报。");
                return;
            }

            if (_currentDraft.Type != ReportTypes.Weekly)
            {
                SetStatus("当前草稿不是周报，不能导出 Excel。");
                return;
            }

            SyncCurrentDraftFromEditors();
            await _reportGeneration.SaveDraftAsync(_currentDraft);

            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = exportSchoolTemplate ? "选择学校周报 Excel 导出位置" : "选择企业周报 Excel 导出位置",
                UseDescriptionForTitle = true,
                SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            {
                SetStatus("已取消导出周报 Excel。");
                return;
            }

            var reportHistory = await _evidenceStore.ListReportDraftsAsync(
                ReportTypes.Weekly,
                endDate: _currentDraft.EndDate);
            var path = exportSchoolTemplate
                ? _weeklyExcelExporter.ExportSchool(_currentDraft, dialog.SelectedPath, reportHistory)
                : _weeklyExcelExporter.ExportEnterprise(_currentDraft, dialog.SelectedPath, reportHistory);
            SetStatus($"{(exportSchoolTemplate ? "学校" : "企业")}周报 Excel 已导出：{path}");
        }
        catch (Exception ex)
        {
            SetStatus($"导出周报 Excel 失败：{ex.Message}");
        }
    }

    private async void OnSaveDraftClicked(object sender, RoutedEventArgs e)
    {
        await SaveCurrentDraftAsync(ReportStatuses.Draft);
    }

    private async void OnConfirmDraftClicked(object sender, RoutedEventArgs e)
    {
        await SaveCurrentDraftAsync(ReportStatuses.Confirmed);
    }

    private async Task SaveCurrentDraftAsync(string status)
    {
        if (_currentDraft is null)
        {
            SetStatus("还没有可保存的报告草稿。");
            return;
        }

        if (_currentDraft.Status == ReportStatuses.Confirmed && status != ReportStatuses.Confirmed)
        {
            SetStatus("已确认报告不能直接改为草稿，请先另存新草稿。");
            return;
        }

        SyncCurrentDraftFromEditors();
        _currentDraft.Status = status;
        if (status == ReportStatuses.Confirmed)
        {
            _currentDraft.ConfirmedAt ??= _timeProvider.Now;
        }
        await _reportGeneration.SaveDraftAsync(_currentDraft);
        await RefreshReportHistoryAsync();
        SetReportEditorsReadOnly(status == ReportStatuses.Confirmed);
        SetStatus(status == ReportStatuses.Confirmed ? "报告已确认定稿。" : "草稿已保存。");
    }

    private async void OnLoadDraftClicked(object sender, RoutedEventArgs e)
    {
        if (ReportHistoryGrid.SelectedItem is not ReportDraft selected)
        {
            SetStatus("请先选择历史草稿。");
            return;
        }

        await LoadReportDraftAsync(selected.Id);
    }

    private async Task LoadReportDraftAsync(string? draftId)
    {
        if (string.IsNullOrWhiteSpace(draftId))
        {
            throw new InvalidOperationException("报告草稿 ID 不能为空。");
        }

        var draft = await _evidenceStore.LoadReportDraftByIdAsync(draftId);
        if (draft is null)
        {
            SetStatus("未找到选中的历史草稿。");
            return;
        }

        _currentDraft = draft;
        LoadDraftIntoEditors(draft);
        SetReportEditorsReadOnly(draft.Status == ReportStatuses.Confirmed);
        var evidence = await _evidenceStore.ReadEvidenceAsync(draft.StartDate, draft.EndDate);
        foreach (var item in evidence)
        {
            item.Included = draft.EvidenceIds.Contains(item.Id);
        }

        _viewModel.ReplaceReportEvidence(evidence);
        _viewModel.ReplaceReportClusters(_clusterer.Cluster(evidence));
        ReportEvidenceGrid.Items.Refresh();
        ReportClusterGrid.Items.Refresh();
        SetStatus($"已载入{draft.DisplayType}历史：{draft.DisplayRange}。");
    }

    private async void OnDeleteDraftClicked(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedDraftsAsync();
    }

    private async void OnReportHistoryGridKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete)
        {
            return;
        }

        e.Handled = true;
        await DeleteSelectedDraftsAsync();
    }

    private async Task DeleteSelectedDraftsAsync()
    {
        var selected = ReportHistoryGrid.SelectedItems.OfType<ReportDraft>().DistinctBy(item => item.Id).ToList();
        if (selected.Count == 0)
        {
            SetStatus("请先选择要删除的历史草稿。");
            return;
        }

        var result = System.Windows.MessageBox.Show(
            this,
            $"确定删除选中的 {selected.Count} 个历史草稿吗？",
            "批量删除历史草稿",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var selectedIds = selected.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletedCount = await _evidenceStore.DeleteReportDraftsAsync(selectedIds);

        if (_currentDraft is not null && selectedIds.Contains(_currentDraft.Id))
        {
            _currentDraft = null;
            ReportContentBox.Text = "";
            WorkSummaryBox.Text = "";
            ProblemsBox.Text = "";
            NextWeekPlanBox.Text = "";
            SupportSuggestionsBox.Text = "";
            SetReportEditorsReadOnly(false);
        }

        await RefreshReportHistoryAsync();
        SetStatus(deletedCount > 0 ? $"已删除 {deletedCount} 个历史草稿。" : "未找到要删除的历史草稿。");
    }

    private void OnNewDraftFromConfirmedClicked(object sender, RoutedEventArgs e)
    {
        CreateNewDraftFromCurrent();
    }

    private async Task NewDraftFromConfirmedAsync(string? draftId)
    {
        if (!string.IsNullOrWhiteSpace(draftId)
            && !string.Equals(_currentDraft?.Id, draftId, StringComparison.OrdinalIgnoreCase))
        {
            await LoadReportDraftAsync(draftId);
        }
        CreateNewDraftFromCurrent();
    }

    private void CreateNewDraftFromCurrent()
    {
        if (_currentDraft is null)
        {
            SetStatus("请先载入一个已确认报告。");
            return;
        }

        _currentDraft = new ReportDraft
        {
            Type = _currentDraft.Type,
            StartDate = _currentDraft.StartDate,
            EndDate = _currentDraft.EndDate,
            Content = ReportContentBox.Text,
            DailySummary = _currentDraft.DailySummary,
            WorkSummary = _currentDraft.WorkSummary,
            Problems = _currentDraft.Problems,
            NextWeekPlan = _currentDraft.NextWeekPlan,
            SupportAndSuggestions = _currentDraft.SupportAndSuggestions,
            EvidenceIds = _currentDraft.EvidenceIds.ToList(),
            EvidenceClusterIds = _currentDraft.EvidenceClusterIds.ToList(),
            SourceDailyDraftIds = _currentDraft.SourceDailyDraftIds.ToList(),
            TemplateId = _currentDraft.TemplateId,
            TemplateName = _currentDraft.TemplateName,
            Status = ReportStatuses.Draft,
            CreatedAt = _timeProvider.Now,
            UpdatedAt = _timeProvider.Now
        };
        SetReportEditorsReadOnly(false);
        _lastReportEditorEditSource = ReportEditorEditSource.Structured;
        SetStatus("已从当前报告创建新的草稿，保存后进入历史。");
    }

    private void LoadDraftIntoEditors(ReportDraft draft)
    {
        _isLoadingReportEditors = true;
        try
        {
            if (draft.Type == ReportTypes.Weekly)
            {
                var sections = WeeklyReportSectionParser.FromDraft(draft);
                var hasContent = !string.IsNullOrWhiteSpace(draft.Content);
                WorkSummaryBox.Text = sections.WorkSummary;
                ProblemsBox.Text = sections.Problems;
                NextWeekPlanBox.Text = sections.NextWeekPlan;
                SupportSuggestionsBox.Text = sections.SupportAndSuggestions;
                ReportContentBox.Text = !hasContent
                    ? WeeklyReportSectionParser.BuildWeeklyMarkdown(
                        WorkSummaryBox.Text,
                        ProblemsBox.Text,
                        NextWeekPlanBox.Text,
                        SupportSuggestionsBox.Text)
                    : draft.Content;
                _lastReportEditorEditSource = hasContent
                    ? ReportEditorEditSource.Markdown
                    : ReportEditorEditSource.Structured;
                return;
            }

            var hasDailyContent = !string.IsNullOrWhiteSpace(draft.Content);
            WorkSummaryBox.Text = string.IsNullOrWhiteSpace(draft.DailySummary)
                ? WeeklyReportSectionParser.ParseDailySummary(draft.Content)
                : draft.DailySummary;
            ProblemsBox.Text = "";
            NextWeekPlanBox.Text = "";
            SupportSuggestionsBox.Text = "";
            ReportContentBox.Text = !hasDailyContent
                ? WeeklyReportSectionParser.BuildDailyMarkdown(WorkSummaryBox.Text)
                : draft.Content;
            _lastReportEditorEditSource = hasDailyContent
                ? ReportEditorEditSource.Markdown
                : ReportEditorEditSource.Structured;
        }
        finally
        {
            _isLoadingReportEditors = false;
        }
    }

    private void SyncCurrentDraftFromEditors()
    {
        if (_currentDraft is null)
        {
            return;
        }

        if (_currentDraft.Status == ReportStatuses.Confirmed)
        {
            return;
        }

        if (_currentDraft.Type == ReportTypes.Weekly)
        {
            if (_lastReportEditorEditSource == ReportEditorEditSource.Markdown)
            {
                var sections = WeeklyReportSectionParser.Parse(ReportContentBox.Text);
                _currentDraft.WorkSummary = sections.WorkSummary;
                _currentDraft.Problems = sections.Problems;
                _currentDraft.NextWeekPlan = sections.NextWeekPlan;
                _currentDraft.SupportAndSuggestions = sections.SupportAndSuggestions;
                _currentDraft.Content = ReportContentBox.Text;
                SetStructuredEditorsSilently(sections);
                return;
            }

            SaveStructuredWeeklyEditorsToDraft();
            return;
        }

        if (_lastReportEditorEditSource == ReportEditorEditSource.Markdown)
        {
            _currentDraft.Content = ReportContentBox.Text;
            _currentDraft.DailySummary = WeeklyReportSectionParser.ParseDailySummary(ReportContentBox.Text);
            SetTextSilently(WorkSummaryBox, _currentDraft.DailySummary);
            return;
        }

        _currentDraft.DailySummary = WorkSummaryBox.Text;
        _currentDraft.Content = WeeklyReportSectionParser.BuildDailyMarkdown(_currentDraft.DailySummary);
        SetTextSilently(ReportContentBox, _currentDraft.Content);
    }

    private void SaveStructuredWeeklyEditorsToDraft()
    {
        if (_currentDraft is null)
        {
            return;
        }

        _currentDraft.WorkSummary = WorkSummaryBox.Text;
        _currentDraft.Problems = ProblemsBox.Text;
        _currentDraft.NextWeekPlan = NextWeekPlanBox.Text;
        _currentDraft.SupportAndSuggestions = SupportSuggestionsBox.Text;
        _currentDraft.Content = WeeklyReportSectionParser.BuildWeeklyMarkdown(
            _currentDraft.WorkSummary,
            _currentDraft.Problems,
            _currentDraft.NextWeekPlan,
            _currentDraft.SupportAndSuggestions);
        SetTextSilently(ReportContentBox, _currentDraft.Content);
    }

    private void SetStructuredEditorsSilently(WeeklyReportSections sections)
    {
        _isLoadingReportEditors = true;
        try
        {
            WorkSummaryBox.Text = sections.WorkSummary;
            ProblemsBox.Text = sections.Problems;
            NextWeekPlanBox.Text = sections.NextWeekPlan;
            SupportSuggestionsBox.Text = sections.SupportAndSuggestions;
        }
        finally
        {
            _isLoadingReportEditors = false;
        }
    }

    private void SetTextSilently(System.Windows.Controls.TextBox textBox, string text)
    {
        _isLoadingReportEditors = true;
        try
        {
            textBox.Text = text;
        }
        finally
        {
            _isLoadingReportEditors = false;
        }
    }

    private void OnReportMarkdownTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoadingReportEditors && _currentDraft?.Status != ReportStatuses.Confirmed)
        {
            _lastReportEditorEditSource = ReportEditorEditSource.Markdown;
        }
    }

    private void OnReportStructuredTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoadingReportEditors && _currentDraft?.Status != ReportStatuses.Confirmed)
        {
            _lastReportEditorEditSource = ReportEditorEditSource.Structured;
        }
    }

    private void SetReportEditorsReadOnly(bool isReadOnly)
    {
        ReportContentBox.IsReadOnly = isReadOnly;
        WorkSummaryBox.IsReadOnly = isReadOnly;
        ProblemsBox.IsReadOnly = isReadOnly;
        NextWeekPlanBox.IsReadOnly = isReadOnly;
        SupportSuggestionsBox.IsReadOnly = isReadOnly;
    }

    private void OnAddTemplateClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.ReportTemplates.Add(new ReportTemplateEditor
        {
            Name = "新模板",
            ReportTypeScope = ReportTemplateScopes.Both,
            PromptInstructions = "请填写这个模板的报告侧重点。"
        });
        SetStatus("已新增模板，保存后生效。");
    }

    private void OnDeleteTemplateClicked(object sender, RoutedEventArgs e)
    {
        if (ReportTemplateGrid.SelectedItem is not ReportTemplateEditor selected)
        {
            SetStatus("请先选择要删除的模板。");
            return;
        }

        if (selected.IsBuiltIn)
        {
            SetStatus("内置模板不能删除，可以直接编辑提示词要求。");
            return;
        }

        _viewModel.ReportTemplates.Remove(selected);
        SetStatus("已删除模板，保存后生效。");
    }

    private async void OnSaveTemplatesClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ReportTemplateGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ReportTemplateGrid.CommitEdit(DataGridEditingUnit.Row, true);
            await _settingsStore.SaveReportTemplatesAsync(_viewModel.ReportTemplates.Select(editor => editor.ToTemplate()).ToList());
            await LoadReportTemplatesAsync();
            SetStatus("报告模板已保存。");
        }
        catch (Exception ex)
        {
            SetStatus($"保存模板失败：{ex.Message}");
        }
    }

    private async Task SaveEvidenceEditsAsync()
    {
        await _evidenceStore.UpsertEvidenceAsync(_viewModel.Evidence);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}

public sealed class NonBlankStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string text && !string.IsNullOrWhiteSpace(text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class ConfigurationViewModel
{
    public ObservableCollection<ReminderRuleEditor> Reminders { get; } = [];
    public ObservableCollection<HolidayAdjustmentEditor> HolidayAdjustments { get; } = [];
    public ObservableCollection<EvidenceItem> Evidence { get; } = [];
    public ObservableCollection<TimelineGroup> TimelineGroups { get; } = [];
    public ObservableCollection<EvidenceItem> ReportEvidence { get; } = [];
    public ObservableCollection<EvidenceCluster> ReportClusters { get; } = [];
    public ObservableCollection<ReportDraft> ReportDrafts { get; } = [];
    public ObservableCollection<ReportTemplateEditor> ReportTemplates { get; } = [];
    public IReadOnlyList<ReminderScheduleKind> ScheduleKinds { get; } =
    [
        ReminderScheduleKind.Workdays,
        ReminderScheduleKind.Daily,
        ReminderScheduleKind.SpecificWeekdays,
        ReminderScheduleKind.Once
    ];
    public IReadOnlyList<HolidayAdjustmentKind> HolidayKinds { get; } =
    [
        HolidayAdjustmentKind.Holiday,
        HolidayAdjustmentKind.MakeUpWorkday
    ];
    public IReadOnlyList<string> TemplateScopes { get; } =
    [
        ReportTemplateScopes.Both,
        ReportTemplateScopes.Daily,
        ReportTemplateScopes.Weekly
    ];
    public IReadOnlyList<ModelQualityMode> QualityModes { get; } =
    [
        ModelQualityMode.LowCost,
        ModelQualityMode.Balanced,
        ModelQualityMode.HighQuality
    ];

    public void ReplaceReminders(IEnumerable<ReminderRuleEditor> reminders)
    {
        Reminders.Clear();
        foreach (var reminder in reminders.OrderBy(item => item.TimeText))
        {
            Reminders.Add(reminder);
        }
    }

    public void ReplaceHolidayAdjustments(IEnumerable<HolidayAdjustmentEditor> adjustments)
    {
        HolidayAdjustments.Clear();
        foreach (var adjustment in adjustments.OrderBy(item => item.DateText))
        {
            HolidayAdjustments.Add(adjustment);
        }
    }

    public void ReplaceEvidence(IEnumerable<EvidenceItem> evidence)
    {
        Evidence.Clear();
        foreach (var item in evidence.OrderBy(item => item.CapturedAt))
        {
            Evidence.Add(item);
        }
    }

    public void ReplaceTimelineGroups(IEnumerable<TimelineGroup> groups)
    {
        TimelineGroups.Clear();
        foreach (var group in groups)
        {
            TimelineGroups.Add(group);
        }
    }

    public void ReplaceReportEvidence(IEnumerable<EvidenceItem> evidence)
    {
        ReportEvidence.Clear();
        foreach (var item in evidence.OrderBy(item => item.CapturedAt))
        {
            ReportEvidence.Add(item);
        }
    }

    public void ReplaceReportClusters(IEnumerable<EvidenceCluster> clusters)
    {
        ReportClusters.Clear();
        foreach (var cluster in clusters.OrderByDescending(item => item.EvidenceIds.Count).ThenBy(item => item.Name))
        {
            ReportClusters.Add(cluster);
        }
    }

    public void ReplaceReportDrafts(IEnumerable<ReportDraft> drafts)
    {
        ReportDrafts.Clear();
        foreach (var draft in drafts.OrderByDescending(item => item.UpdatedAt))
        {
            ReportDrafts.Add(draft);
        }
    }

    public void ReplaceReportTemplates(IEnumerable<ReportTemplateEditor> templates)
    {
        ReportTemplates.Clear();
        foreach (var template in templates.OrderByDescending(item => item.IsBuiltIn).ThenBy(item => item.Name))
        {
            ReportTemplates.Add(template);
        }
    }
}

public sealed class TimelineGroup
{
    public TimelineGroup(string header, IEnumerable<EvidenceItem> items)
    {
        Header = header;
        foreach (var item in items.OrderBy(item => item.CapturedAt))
        {
            Items.Add(new TimelineEntry(item));
        }
    }

    public string Header { get; }
    public ObservableCollection<TimelineEntry> Items { get; } = [];
    public string CountText => $"{Items.Count} 条";
}

public sealed class ReportTemplateEditor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ReportTypeScope { get; set; } = ReportTemplateScopes.Both;
    public string PromptInstructions { get; set; } = "";
    public bool IsBuiltIn { get; set; }

    public static ReportTemplateEditor FromTemplate(ReportTemplate template)
    {
        return new ReportTemplateEditor
        {
            Id = template.Id,
            Name = template.Name,
            ReportTypeScope = template.ReportTypeScope,
            PromptInstructions = template.PromptInstructions,
            IsBuiltIn = template.IsBuiltIn
        };
    }

    public ReportTemplate ToTemplate()
    {
        return new ReportTemplate
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
            Name = string.IsNullOrWhiteSpace(Name) ? "未命名模板" : Name.Trim(),
            ReportTypeScope = ReportTypeScope,
            PromptInstructions = PromptInstructions.Trim(),
            IsBuiltIn = IsBuiltIn
        };
    }
}

public sealed class ReminderRuleEditor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool IsEnabled { get; set; } = true;
    public string TimeText { get; set; } = "09:00";
    public string Message { get; set; } = "";
    public ReminderScheduleKind ScheduleKind { get; set; } = ReminderScheduleKind.Workdays;
    public string WeekdaysText { get; set; } = "";
    public string OnceDateText { get; set; } = "";
    public int AdvanceMinutes { get; set; }
    public string ScheduleKindDisplay => ScheduleKind switch
    {
        ReminderScheduleKind.Daily => "每天",
        ReminderScheduleKind.SpecificWeekdays => "指定星期",
        ReminderScheduleKind.Once => "单次",
        _ => "工作日"
    };
    public string AdvanceMinutesDisplay => AdvanceMinutes <= 0 ? "准点" : $"提前 {AdvanceMinutes} 分钟";

    public static ReminderRuleEditor FromRule(ReminderRule rule)
    {
        return new ReminderRuleEditor
        {
            Id = rule.Id,
            IsEnabled = rule.IsEnabled,
            TimeText = rule.Time.ToString("HH:mm"),
            Message = rule.Message,
            ScheduleKind = rule.ScheduleKind,
            WeekdaysText = string.Join(",", rule.Weekdays.OrderBy(day => (int)day).Select(ToWeekdayText)),
            OnceDateText = rule.OnceDate?.ToString("yyyy-MM-dd") ?? "",
            AdvanceMinutes = rule.AdvanceMinutes
        };
    }

    public ReminderRule ToRule()
    {
        if (!TimeOnly.TryParse(TimeText.Trim(), out var time))
        {
            throw new InvalidOperationException($"提醒时间格式不正确：{TimeText}，请使用 HH:mm。");
        }

        DateOnly? onceDate = null;
        if (!string.IsNullOrWhiteSpace(OnceDateText))
        {
            if (!DateOnly.TryParse(OnceDateText.Trim(), out var parsedDate))
            {
                throw new InvalidOperationException($"单次日期格式不正确：{OnceDateText}，请使用 yyyy-MM-dd。");
            }

            onceDate = parsedDate;
        }

        return new ReminderRule
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
            IsEnabled = IsEnabled,
            Time = time,
            Message = Message.Trim(),
            ScheduleKind = ScheduleKind,
            Weekdays = ParseWeekdays(WeekdaysText),
            OnceDate = onceDate,
            AdvanceMinutes = Math.Max(0, AdvanceMinutes)
        };
    }

    private static HashSet<DayOfWeek> ParseWeekdays(string value)
    {
        HashSet<DayOfWeek> weekdays = [];
        foreach (var part in value.Split([',', '，', ' ', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = part.Trim().ToLowerInvariant();
            var day = normalized switch
            {
                "1" or "一" or "周一" or "星期一" or "mon" or "monday" => DayOfWeek.Monday,
                "2" or "二" or "周二" or "星期二" or "tue" or "tuesday" => DayOfWeek.Tuesday,
                "3" or "三" or "周三" or "星期三" or "wed" or "wednesday" => DayOfWeek.Wednesday,
                "4" or "四" or "周四" or "星期四" or "thu" or "thursday" => DayOfWeek.Thursday,
                "5" or "五" or "周五" or "星期五" or "fri" or "friday" => DayOfWeek.Friday,
                "6" or "六" or "周六" or "星期六" or "sat" or "saturday" => DayOfWeek.Saturday,
                "0" or "7" or "日" or "天" or "周日" or "星期日" or "周天" or "星期天" or "sun" or "sunday" => DayOfWeek.Sunday,
                _ => throw new InvalidOperationException($"无法识别指定星期：{part}")
            };
            weekdays.Add(day);
        }

        return weekdays;
    }

    private static string ToWeekdayText(DayOfWeek day)
    {
        return day switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            DayOfWeek.Sunday => "周日",
            _ => ""
        };
    }
}

public sealed class HolidayAdjustmentEditor
{
    public string DateText { get; set; } = "";
    public HolidayAdjustmentKind Kind { get; set; } = HolidayAdjustmentKind.Holiday;
    public string Name { get; set; } = "";
    public string KindDisplay => Kind == HolidayAdjustmentKind.MakeUpWorkday ? "调休上班" : "节假日";

    public static HolidayAdjustmentEditor FromAdjustment(HolidayAdjustment adjustment)
    {
        return new HolidayAdjustmentEditor
        {
            DateText = adjustment.Date.ToString("yyyy-MM-dd"),
            Kind = adjustment.Kind,
            Name = adjustment.Name
        };
    }

    public HolidayAdjustment ToAdjustment()
    {
        if (!DateOnly.TryParse(DateText.Trim(), out var date))
        {
            throw new InvalidOperationException($"日期规则格式不正确：{DateText}，请使用 yyyy-MM-dd。");
        }

        return new HolidayAdjustment
        {
            Date = date,
            Kind = Kind,
            Name = Name.Trim()
        };
    }
}
