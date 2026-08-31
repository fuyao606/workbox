﻿﻿﻿﻿﻿﻿using System.Windows;
using Timenotifier.Application;
using Timenotifier.Views;

namespace Timenotifier;

public partial class App : System.Windows.Application
{
    private AppUiCoordinator _uiCoordinator = null!;
    private AppLifetimeCoordinator _lifetimeCoordinator = null!;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var uiLoopStartup = ParseUiLoopStartupOptions(e.Args);
        _uiCoordinator = null!;
        var services = new AppBootstrapper().Bootstrap(
            uiLoopStartup.DataRoot,
            ShowReminder,
            async () => await _uiCoordinator.StartDailyReportAutomationAsync(),
            uiLoopStartup.AssistantFixture);
        _uiCoordinator = new AppUiCoordinator(services, Dispatcher, uiLoopStartup.IsUiLoopMode);

        _lifetimeCoordinator = new AppLifetimeCoordinator(
            services,
            Dispatcher,
            _uiCoordinator.CreateFloatingBall,
            _uiCoordinator.InitializeCaptureServicesAsync,
            _uiCoordinator.OpenConfiguration);
        _uiCoordinator.AttachLifetimeCoordinator(_lifetimeCoordinator);
        _lifetimeCoordinator.Start(uiLoopStartup.OpenConfiguration, uiLoopStartup.Section);
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _lifetimeCoordinator?.Stop(_uiCoordinator?.HotkeyService);
    }

    private static UiLoopStartupOptions ParseUiLoopStartupOptions(string[] args)
    {
        var window = GetArgumentValue(args, "--ui-loop-window");
        var section = GetArgumentValue(args, "--ui-loop-section");
        var dataRoot = GetArgumentValue(args, "--ui-loop-data-root");
        var assistantFixture = GetArgumentValue(args, "--ui-loop-assistant-fixture");
        var openConfiguration = string.Equals(window, "ConfigurationWindow", StringComparison.OrdinalIgnoreCase)
            || string.Equals(window, "Configuration", StringComparison.OrdinalIgnoreCase);

        return new UiLoopStartupOptions(
            openConfiguration,
            ParseConfigurationSection(section),
            !string.IsNullOrWhiteSpace(window) || !string.IsNullOrWhiteSpace(section) || !string.IsNullOrWhiteSpace(dataRoot),
            dataRoot is { Length: > 0 }
                ? Environment.ExpandEnvironmentVariables(dataRoot)
                : null,
            assistantFixture);
    }

    private static string? GetArgumentValue(string[] args, string name)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith($"{name}=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(name.Length + 1)..];
            }
        }

        return null;
    }

    private static ConfigurationSection ParseConfigurationSection(string? section)
    {
        return section?.ToLowerInvariant() switch
        {
            "assistant" => ConfigurationSection.Assistant,
            "reminder" or "reminders" => ConfigurationSection.Reminders,
            "model" => ConfigurationSection.Model,
            "evidence" => ConfigurationSection.Evidence,
            "review" => ConfigurationSection.Review,
            "report" or "reports" => ConfigurationSection.Reports,
            "capture" => ConfigurationSection.Capture,
            "task" or "tasks" => ConfigurationSection.Tasks,
            _ => ConfigurationSection.Assistant
        };
    }

    private sealed record UiLoopStartupOptions(
        bool OpenConfiguration,
        ConfigurationSection Section,
        bool IsUiLoopMode,
        string? DataRoot,
        string? AssistantFixture);

    private void ShowReminder(
        string message,
        string title = "时间提醒")
    {
        _lifetimeCoordinator.ShowReminder(message, title);
    }
}
