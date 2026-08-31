﻿﻿﻿﻿﻿using System.Reflection;
using Xunit;
using System.Text.RegularExpressions;
using Timenotifier.Services.Automation;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Reporting;
using Timenotifier.Services.Scheduling;
using Timenotifier.Services.Settings;
using Timenotifier.Views;

namespace Timenotifier.Tests;

public sealed class AppStructureTests
{
    private static readonly string RepositoryRoot = GetRepositoryRoot();

    [Fact]
    public void Main_windows_are_grouped_under_views_namespace()
    {
        var expectedWindowTypes = new[]
        {
            "Timenotifier.Views.MainWindow",
            "Timenotifier.Views.ConfigurationWindow",
            "Timenotifier.Views.ManualEvidenceWindow",
            "Timenotifier.Views.ReminderPopupWindow",
            "Timenotifier.Views.AutomationReviewWindow"
        };

        var assembly = typeof(App).Assembly;

        foreach (var typeName in expectedWindowTypes)
        {
            Assert.NotNull(assembly.GetType(typeName));
        }
    }

    [Fact]
    public void Assistant_is_the_first_and_default_configuration_section()
    {
        Assert.Equal(0, (int)ConfigurationSection.Assistant);

        var constructor = typeof(ConfigurationWindow)
            .GetConstructors()
            .Single(item => item.GetParameters().LastOrDefault()?.ParameterType == typeof(ConfigurationSection));
        var initialSection = constructor.GetParameters().Last();

        Assert.True(initialSection.HasDefaultValue);
        Assert.Equal(ConfigurationSection.Assistant, initialSection.DefaultValue);
    }

    [Fact]
    public void Configuration_window_hosts_assistant_page_as_the_first_tab()
    {
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot, "Views", "ConfigurationWindow.xaml"));
        var firstTabIndex = xaml.IndexOf("<TabItem", StringComparison.Ordinal);
        var assistantTabIndex = xaml.IndexOf("AutomationProperties.AutomationId=\"TabAssistant\"", StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "Views", "Assistant", "AssistantPage.xaml")));
        Assert.True(firstTabIndex >= 0);
        Assert.True(assistantTabIndex >= firstTabIndex && assistantTabIndex - firstTabIndex < 300);
        Assert.NotNull(typeof(App).Assembly.GetType("Timenotifier.Views.Assistant.AssistantPage"));
    }

    [Fact]
    public void Normal_and_ui_loop_configuration_entry_points_support_assistant()
    {
        var appSource = File.ReadAllText(Path.Combine(RepositoryRoot, "App.xaml.cs"));
        var coordinatorSource = File.ReadAllText(Path.Combine(RepositoryRoot, "Application", "AppUiCoordinator.cs"));

        Assert.Contains("\"assistant\" => ConfigurationSection.Assistant", appSource);
        Assert.Contains("\"reminder\" or \"reminders\" => ConfigurationSection.Reminders", appSource);
        Assert.Contains("OpenConfiguration(ConfigurationSection.Assistant)", coordinatorSource);
    }

    [Fact]
    public void Root_directory_does_not_contain_main_window_xaml_files()
    {
        var rootWindowFiles = new[]
        {
            "MainWindow.xaml",
            "MainWindow.xaml.cs",
            "ConfigurationWindow.xaml",
            "ConfigurationWindow.xaml.cs",
            "ManualEvidenceWindow.xaml",
            "ManualEvidenceWindow.xaml.cs",
            "ReminderPopupWindow.xaml",
            "ReminderPopupWindow.xaml.cs",
            "AutomationReviewWindow.xaml",
            "AutomationReviewWindow.xaml.cs"
        };

        foreach (var file in rootWindowFiles)
        {
            Assert.False(File.Exists(Path.Combine(RepositoryRoot, file)), $"Root window file should be moved: {file}");
        }
    }

    [Fact]
    public void Application_bootstrapper_and_lifetime_coordinator_exist()
    {
        var assembly = typeof(App).Assembly;

        Assert.NotNull(assembly.GetType("Timenotifier.Application.AppBootstrapper"));
        Assert.NotNull(assembly.GetType("Timenotifier.Application.AppLifetimeCoordinator"));
    }

    [Fact]
    public void App_xaml_cs_delegates_bootstrap_and_lifetime_coordination()
    {
        var appSource = File.ReadAllText(Path.Combine(RepositoryRoot, "App.xaml.cs"));

        Assert.Contains("AppBootstrapper", appSource);
        Assert.Contains("AppLifetimeCoordinator", appSource);
        Assert.Contains("AppUiCoordinator", appSource);
        Assert.DoesNotContain("new EvidenceStore", appSource);
        Assert.DoesNotContain("new ScreenshotCaptureService", appSource);
        Assert.DoesNotContain("new DailyReportAutomationService", appSource);
        Assert.DoesNotContain("new ReminderScheduler", appSource);
        Assert.DoesNotContain("new TrayIconService", appSource);
        Assert.DoesNotContain("ConfigurationWindow(", appSource);
        Assert.DoesNotContain("ManualEvidenceWindow", appSource);
        Assert.DoesNotContain("AutomationReviewWindow", appSource);
        Assert.DoesNotContain("CaptureScreenshotAsync", appSource);
        Assert.DoesNotContain("OpenManualEvidenceAsync", appSource);
        Assert.DoesNotContain("RunDailyReportAutomationAsync", appSource);
        Assert.DoesNotContain("OpenDailyReportLoginBrowserAsync", appSource);
        Assert.DoesNotContain("RegisterManualHotkeyAsync", appSource);
    }

    [Fact]
    public void Lifetime_coordinator_triggers_capture_initialization_once()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "Application", "AppLifetimeCoordinator.cs"));

        Assert.DoesNotContain("SourceInitialized", source);
        Assert.Equal(1, CountOccurrences(source, "initializeCaptureServicesAsync()"));
    }

    [Fact]
    public void Floating_ball_show_does_not_activate_transparent_window()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "Views", "MainWindow.xaml.cs"));

        Assert.DoesNotContain("Activate();", source);
    }

    [Theory]
    [InlineData("Services/Automation", "DailyReportAutomationService.cs")]
    [InlineData("Services/Automation", "PlaywrightCliAutomationRunner.cs")]
    [InlineData("Services/Automation", "StartupRegistrationService.cs")]
    [InlineData("Services/Evidence", "EvidenceStore.cs")]
    [InlineData("Services/Evidence", "EvidenceCollectors.cs")]
    [InlineData("Services/Evidence", "ManualEvidenceService.cs")]
    [InlineData("Services/Evidence", "ManualEvidenceHotkeyService.cs")]
    [InlineData("Services/Evidence", "ScreenshotCaptureService.cs")]
    [InlineData("Services/Evidence", "ScreenshotChangeDetector.cs")]
    [InlineData("Services/Evidence", "ActiveWindowService.cs")]
    [InlineData("Services/Evidence", "LocalOcrService.cs")]
    [InlineData("Services/Evidence", "EvidenceClusterer.cs")]
    [InlineData("Services/Infrastructure", "AppDataPaths.cs")]
    [InlineData("Services/Infrastructure", "TimeProvider.cs")]
    [InlineData("Services/Infrastructure", "TrayIconService.cs")]
    [InlineData("Services/Infrastructure", "ScreenCoordinateMapper.cs")]
    [InlineData("Services/Infrastructure", "FloatingBallDocking.cs")]
    [InlineData("Services/Infrastructure", "ModelClient.cs")]
    [InlineData("Services/Reporting", "ReportGenerationService.cs")]
    [InlineData("Services/Reporting", "ReportPromptBuilder.cs")]
    [InlineData("Services/Reporting", "ReportQualityFilter.cs")]
    [InlineData("Services/Reporting", "ReportTextFormatter.cs")]
    [InlineData("Services/Reporting", "WeeklyReportSections.cs")]
    [InlineData("Services/Reporting", "WeeklyReportExcelExporter.cs")]
    [InlineData("Services/Reporting", "InternshipWeekCalculator.cs")]
    [InlineData("Services/Scheduling", "ReminderScheduler.cs")]
    [InlineData("Services/Scheduling", "ReminderEvaluation.cs")]
    [InlineData("Services/Scheduling", "DefaultReminders.cs")]
    [InlineData("Services/Scheduling", "AutoScreenshotScheduler.cs")]
    [InlineData("Services/Scheduling", "DailyReportAutomationScheduler.cs")]
    [InlineData("Services/Settings", "AppSettingsStore.cs")]
    public void Service_files_are_grouped_by_responsibility(string directory, string fileName)
    {
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, directory, fileName)), $"Expected service file under {directory}: {fileName}");
    }

    [Fact]
    public void Model_types_do_not_reference_ui_or_service_implementations()
    {
        var modelTypes = typeof(Timenotifier.Models.ModelProviderSettings).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "Timenotifier.Models")
            .ToArray();

        Assert.NotEmpty(modelTypes);

        foreach (var type in modelTypes)
        {
            var referencedTypeNames = GetReferencedTypeNames(type);

            Assert.DoesNotContain("System.Windows", referencedTypeNames);
            Assert.DoesNotContain("System.Windows.Forms", referencedTypeNames);
            Assert.DoesNotContain("Timenotifier.Services", referencedTypeNames);
            Assert.DoesNotContain("Timenotifier.Views", referencedTypeNames);
        }
    }

    [Fact]
    public void Views_do_not_reference_python_agent_runtime_internals()
    {
        var viewSourceFiles = Directory.GetFiles(Path.Combine(RepositoryRoot, "Views"), "*.cs", SearchOption.TopDirectoryOnly);

        Assert.NotEmpty(viewSourceFiles);

        foreach (var file in viewSourceFiles)
        {
            var source = File.ReadAllText(file);

            Assert.DoesNotContain("timenotifier_agent", source);
            Assert.DoesNotContain("AgentRuntime", source);
            Assert.DoesNotContain("workflows.py", source);
        }
    }

    [Fact]
    public void Documentation_describes_new_structure()
    {
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot, "README.md"));

        Assert.Contains("Views", readme);
        Assert.Contains("Application", readme);
        Assert.Contains("Services/Automation", readme);
        Assert.Contains("Services/Evidence", readme);
        Assert.Contains("Services/Infrastructure", readme);
        Assert.Contains("Services/Reporting", readme);
        Assert.Contains("Services/Scheduling", readme);
        Assert.Contains("Services/Settings", readme);
    }

    [Fact]
    public void Material_theme_is_loaded_globally_from_the_fixed_package()
    {
        var project = File.ReadAllText(Path.Combine(RepositoryRoot, "Timenotifier.csproj"));
        var appXaml = File.ReadAllText(Path.Combine(RepositoryRoot, "App.xaml"));

        Assert.Contains("<PackageReference Include=\"MaterialDesignThemes\" Version=\"5.3.2\" />", project);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "Views", "Themes", "TimenotifierMaterialTheme.xaml")));
        Assert.Contains("materialDesign:BundledTheme", appXaml);
        Assert.Contains("MaterialDesign3.Defaults.xaml", appXaml);
        Assert.Contains("Views/Themes/TimenotifierMaterialTheme.xaml", appXaml);
    }

    [Fact]
    public void Material_refresh_preserves_all_xaml_name_and_event_contracts()
    {
        var expectedNames = new Dictionary<string, string[]>
        {
            ["Views/AutomationReviewWindow.xaml"] = ["StatusText", "DailyTextBox", "ScreenshotTitleText", "ScreenshotImage", "CancelButton", "ConfirmButton"],
            ["Views/ConfigurationWindow.xaml"] =
            [
                "ButtonChrome", "ButtonChrome", "ButtonChrome", "InputChrome", "PART_ContentHost", "InputChrome", "PART_ContentHost", "PART_SelectedContentHost", "CurrentSectionTitleText", "AssistantHomeButton", "ToolsMenuButton", "ToolsMenuPopup", "NavigationList", "SystemNavigationList",
                "Sections", "AssistantHost", "ReminderScrollViewer", "SnoozeMinutesBox", "ReminderGrid", "ScheduleKindColumn", "HolidayGrid", "HolidayKindColumn", "ModelScrollViewer", "QualityModeBox", "TextBaseUrlBox", "TextApiKeyBox", "TextModelBox", "VisionBaseUrlBox", "VisionApiKeyBox", "VisionModelBox", "UseVisionBox", "EvidenceScrollViewer", "EvidenceRangeBox", "EvidenceGrid", "ReviewScrollViewer", "TimelineRangeBox", "TimelineTotalText", "TimelineIncludedText", "TimelineScreenshotText", "TimelineManualText", "TimelineOcrText", "TimelineWindowText", "ReportScrollViewer", "ReportTypeBox", "ReportEvidenceGrid", "ReportClusterGrid", "ReportHistoryGrid", "ReportContentBox", "WorkSummaryBox", "ProblemsBox", "NextWeekPlanBox", "SupportSuggestionsBox", "ReportTemplateBox", "ReportTemplateGrid", "TemplateScopeColumn", "CaptureScrollViewer", "AutoScreenshotEnabledBox", "StartWithWindowsBox", "OcrEnabledBox", "ScreenshotIntervalBox", "MaxAutoScreenshotsBox", "ManualHotkeyBox", "ScreenshotDirectoryBox", "DefaultTagsBox", "PrivacyFiltersBox", "TaskScrollViewer", "DailyAutomationEnabledBox", "DailyAutomationRunTimeBox", "DailyAutomationUrlBox", "DailyAutomationSessionBox", "DailyAutomationProfileBox", "DailyAutomationProjectBox", "DailyAutomationWorkloadBox", "DailyAutomationTaskBox", "DailyAutomationTypeBox", "StatusText"
            ],
            ["Views/MainWindow.xaml"] = ["FloatingSurface", "ExpandedContent", "ClockText", "DockedContent", "DockedHandle"],
            ["Views/ManualEvidenceWindow.xaml"] = ["PromptText", "ContentBox", "TagsBox"],
            ["Views/ReminderPopupWindow.xaml"] = ["TitleText", "MessageText"],
            ["Views/Assistant/AssistantPage.xaml"] = ["Chrome", "NewSessionButton", "DeleteSessionButton", "SessionList", "ModuleCombo", "RequestStateText", "MessagesScrollViewer", "MessagesPanel", "AttachmentPanel", "AttachmentItems", "PendingActionCard", "PendingActionSummary", "ConfirmActionButton", "DeclineActionButton", "ComposerTextBox", "AddAttachmentButton", "CancelButton", "SendButton"]
        };
        var expectedEvents = new Dictionary<string, string[]>
        {
            ["Views/AutomationReviewWindow.xaml"] = ["Click=\"OnCancelClicked\"", "Click=\"OnConfirmClicked\""],
            ["Views/ConfigurationWindow.xaml"] =
            [
                "Click=\"OnAddReminderClicked\"", "Click=\"OnDeleteReminderClicked\"", "Click=\"OnSaveRemindersClicked\"", "Checked=\"{Binding IsEnabled, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", "Click=\"OnAddHolidayClicked\"", "Click=\"OnDeleteHolidayClicked\"", "Click=\"OnTestAnalysisClicked\"", "Click=\"OnTestImageGenerationClicked\"", "Click=\"OnSaveModelClicked\"", "Click=\"OnRefreshEvidenceClicked\"", "Click=\"OnDeleteEvidenceClicked\"", "KeyDown=\"OnEvidenceGridKeyDown\"", "Checked=\"{Binding Included, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", "Click=\"OnEvidenceIncludedClicked\"", "Click=\"OnPreviewEvidenceImageClicked\"", "SelectionChanged=\"OnTimelineRangeChanged\"", "Click=\"OnRefreshTimelineClicked\"", "Click=\"OnOpenEvidenceManagementClicked\"", "Checked=\"{Binding Included, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", "Click=\"OnTimelineIncludedClicked\"", "Click=\"OnPreviewEvidenceImageClicked\"", "Click=\"OnRefreshReportEvidenceClicked\"", "Click=\"OnLoadDraftClicked\"", "Click=\"OnDeleteDraftClicked\"", "Click=\"OnNewDraftFromConfirmedClicked\"", "KeyDown=\"OnReportHistoryGridKeyDown\"", "Click=\"OnGenerateDailyClicked\"", "Click=\"OnGenerateWeeklyClicked\"", "Click=\"OnConfirmDraftClicked\"", "Click=\"OnExportEnterpriseWeeklyExcelClicked\"", "Click=\"OnExportSchoolWeeklyExcelClicked\"", "Click=\"OnCopyMarkdownClicked\"", "Click=\"OnCopyPlainTextClicked\"", "Click=\"OnSaveDraftClicked\"", "Click=\"OnBrowseScreenshotDirectoryClicked\"", "Click=\"OnSaveCaptureSettingsClicked\"", "Click=\"OnOpenDailyAutomationLoginBrowserClicked\"", "Click=\"OnRunDailyAutomationNowClicked\"", "Click=\"OnSaveDailyAutomationSettingsClicked\"", "Click=\"OnBrowseDailyAutomationProfileClicked\"", "Click=\"OnAssistantHomeClicked\"", "Click=\"OnToolsMenuClicked\"", "SelectionChanged=\"OnNavigationSelectionChanged\"", "SelectionChanged=\"OnNavigationSelectionChanged\"", "Click=\"OnToolMenuItemClicked\"", "Click=\"OnToolMenuItemClicked\"", "Click=\"OnToolMenuItemClicked\"", "Click=\"OnToolMenuItemClicked\"", "Click=\"OnToolMenuItemClicked\"", "Click=\"OnToolMenuItemClicked\"", "Click=\"OnToolMenuItemClicked\"", "Click=\"OnCloseClicked\""
            ],
            ["Views/MainWindow.xaml"] = ["MouseLeftButtonDown=\"OnMouseLeftButtonDown\"", "MouseRightButtonUp=\"OnMouseRightButtonUp\"", "MouseLeftButtonDown=\"OnIconMouseLeftButtonDown\"", "MouseLeftButtonUp=\"OnScreenshotIconClick\"", "MouseLeftButtonDown=\"OnIconMouseLeftButtonDown\"", "MouseLeftButtonUp=\"OnHideIconClick\"", "MouseLeftButtonDown=\"OnIconMouseLeftButtonDown\"", "MouseLeftButtonUp=\"OnDockedIconClick\""],
            ["Views/ManualEvidenceWindow.xaml"] = ["Click=\"OnCancelClicked\"", "Click=\"OnSaveClicked\""],
            ["Views/ReminderPopupWindow.xaml"] = ["MouseLeftButtonUp=\"OnPopupMouseLeftButtonUp\""],
            ["Views/Assistant/AssistantPage.xaml"] = ["Click=\"OnNewSessionClicked\"", "Click=\"OnDeleteSessionClicked\"", "SelectionChanged=\"OnSessionSelectionChanged\"", "Click=\"OnOpenModuleClicked\"", "Click=\"OnRemoveAttachmentClicked\"", "Click=\"OnConfirmActionClicked\"", "Click=\"OnDeclineActionClicked\"", "PreviewKeyDown=\"OnComposerPreviewKeyDown\"", "Click=\"OnAddAttachmentClicked\"", "Click=\"OnCancelClicked\"", "Click=\"OnSendClicked\""]
        };

        foreach (var (relativePath, expected) in expectedNames)
        {
            Assert.Equal(expected.OrderBy(value => value, StringComparer.Ordinal), ExtractAttributeValues(ReadRepositoryFile(relativePath), "x:Name"));
        }

        foreach (var (relativePath, expected) in expectedEvents)
        {
            Assert.Equal(expected.OrderBy(value => value, StringComparer.Ordinal), ExtractEventAssignments(ReadRepositoryFile(relativePath)));
        }
    }

    [Fact]
    public void Material_refresh_preserves_automation_shell_and_style_contracts()
    {
        var configuration = ReadRepositoryFile("Views/ConfigurationWindow.xaml");
        var assistant = ReadRepositoryFile("Views/Assistant/AssistantPage.xaml");
        var main = ReadRepositoryFile("Views/MainWindow.xaml");
        var reminder = ReadRepositoryFile("Views/ReminderPopupWindow.xaml");

        foreach (var id in new[] { "TabAssistant", "TabReminders", "TabModel", "TabEvidence", "TabReview", "TabReport", "TabCapture", "TabTask" })
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", configuration);
        }
        foreach (var id in new[] { "ConfigurationAssistantHomeButton", "ConfigurationToolsButton", "ConfigurationToolsPopup", "ConfigurationToolRemindersButton", "ConfigurationToolModelButton", "ConfigurationToolEvidenceButton", "ConfigurationToolReviewButton", "ConfigurationToolReportsButton", "ConfigurationToolCaptureButton", "ConfigurationToolTasksButton" })
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", configuration);
        }
        foreach (var id in new[] { "ConfigurationReminderScrollViewer", "ConfigurationModelScrollViewer", "ConfigurationEvidenceScrollViewer", "ConfigurationReviewScrollViewer", "ConfigurationReportScrollViewer", "ConfigurationCaptureScrollViewer", "ConfigurationTaskScrollViewer" })
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", configuration);
        }
        Assert.DoesNotContain("HeaderPanel", configuration);
        foreach (var id in new[] { "AssistantPage", "NewSessionButton", "DeleteSessionButton", "AssistantSessionList", "AssistantModuleCombo", "OpenAssistantModuleButton", "AssistantRequestState", "AssistantMessagesScrollViewer", "AssistantAttachmentList", "AssistantPendingActionCard", "AssistantPendingActionSummary", "ConfirmAssistantActionButton", "DeclineAssistantActionButton", "AssistantComposerTextBox", "AddAssistantAttachmentButton", "CancelAssistantRequestButton", "SendAssistantMessageButton" })
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", assistant);
        }

        Assert.InRange(Regex.Matches(configuration, @"<Style(?:\s|>)").Count, 0, 12);
        Assert.Contains("Width=\"116\"", main);
        Assert.Contains("Height=\"52\"", main);
        Assert.Contains("AllowsTransparency=\"True\"", main);
        Assert.Contains("Topmost=\"True\"", main);
        Assert.Contains("ShowActivated=\"False\"", main);
        Assert.Contains("Width=\"318\"", reminder);
        Assert.Contains("Height=\"112\"", reminder);
        Assert.Contains("AllowsTransparency=\"True\"", reminder);
        Assert.Contains("Topmost=\"True\"", reminder);

        foreach (var relativePath in new[] { "Views/ConfigurationWindow.xaml", "Views/Assistant/AssistantPage.xaml", "Views/MainWindow.xaml", "Views/ReminderPopupWindow.xaml", "Views/ManualEvidenceWindow.xaml", "Views/AutomationReviewWindow.xaml" })
        {
            var source = ReadRepositoryFile(relativePath);
            Assert.Contains("materialDesign:PackIcon", source);
            Assert.Contains("{StaticResource Timenotifier", source);
            Assert.DoesNotContain("LinearGradientBrush", source);
        }
    }

    [Fact]
    public void Configuration_data_grids_use_only_their_declared_columns()
    {
        var theme = ReadRepositoryFile("Views/Themes/TimenotifierMaterialTheme.xaml");

        Assert.Contains("<Style x:Key=\"ReminderDataGridStyle\"", theme);
        Assert.Contains("<Setter Property=\"AutoGenerateColumns\" Value=\"False\" />", theme);
        Assert.Contains("<Setter Property=\"CanUserAddRows\" Value=\"False\" />", theme);
        Assert.Contains("ConfigurationDataGridColumnHeaderStyle", theme);
        Assert.Contains("ConfigurationDataGridCellStyle", theme);
        Assert.Contains("ConfigurationDataGridRowStyle", theme);
        Assert.Contains("<Setter Property=\"Foreground\" Value=\"{StaticResource TextBrush}\" />", theme);
    }

    [Fact]
    public void Configuration_list_scroll_viewers_leave_horizontal_measurement_to_the_data_grids()
    {
        var configuration = ReadRepositoryFile("Views/ConfigurationWindow.xaml");

        foreach (var scrollViewer in new[] { "ReminderScrollViewer", "EvidenceScrollViewer", "ReportScrollViewer" })
        {
            Assert.Matches(
                $"<ScrollViewer x:Name=\"{scrollViewer}\"[\\s\\S]{{0,280}}HorizontalScrollBarVisibility=\"Disabled\"",
                configuration);
        }
    }

    [Fact]
    public void Configuration_scroll_viewers_enable_smooth_wheel_scrolling()
    {
        var configuration = ReadRepositoryFile("Views/ConfigurationWindow.xaml");
        var behavior = ReadRepositoryFile("Views/SmoothScrollBehavior.cs");

        foreach (var scrollViewer in new[]
                 {
                     "ReminderScrollViewer",
                     "ModelScrollViewer",
                     "EvidenceScrollViewer",
                     "ReviewScrollViewer",
                     "ReportScrollViewer",
                     "CaptureScrollViewer",
                     "TaskScrollViewer"
                 })
        {
            Assert.Matches(
                $"<ScrollViewer x:Name=\"{scrollViewer}\"[\\s\\S]{{0,180}}views:SmoothScrollBehavior\\.IsEnabled=\"True\"",
                configuration);
        }

        Assert.Contains("PreviewMouseWheel", behavior);
        Assert.Contains("DispatcherTimer", behavior);
        Assert.Contains("FindNestedScrollableViewer", behavior);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static IEnumerable<string> ExtractAttributeValues(string source, string attribute)
    {
        return Regex.Matches(source, $@"{Regex.Escape(attribute)}=""([^""]+)""")
            .Select(match => match.Groups[1].Value)
            .OrderBy(value => value, StringComparer.Ordinal);
    }

    private static IEnumerable<string> ExtractEventAssignments(string source)
    {
        return Regex.Matches(source, @"(?:Click|Checked|Unchecked|SelectionChanged|TextChanged|KeyDown|PreviewKeyDown|MouseLeftButtonDown|MouseLeftButtonUp|MouseRightButtonUp)=""[^""]+""")
            .Select(match => match.Value)
            .OrderBy(value => value, StringComparer.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Timenotifier.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static string GetReferencedTypeNames(Type type)
    {
        var referencedTypes = new List<Type>();
        referencedTypes.AddRange(type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Select(property => property.PropertyType));
        referencedTypes.AddRange(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Select(field => field.FieldType));
        referencedTypes.AddRange(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType));

        return string.Join("\n", referencedTypes.Select(referencedType => referencedType.FullName ?? referencedType.Name));
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
