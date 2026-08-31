using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Timenotifier.Models;
using Xunit;
using Timenotifier.Services.Automation;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Scheduling;
using Timenotifier.Services.Settings;

namespace Timenotifier.Tests;

public sealed class DailyReportAutomationTests
{
    [Fact]
    public async Task Settings_default_to_disabled_daily_report_task()
    {
        var root = CreateTempDirectory();
        try
        {
            var settings = await new AppSettingsStore(new AppDataPaths(root)).LoadDailyReportAutomationSettingsAsync();

            Assert.False(settings.IsEnabled);
            Assert.Equal(new TimeOnly(17, 50), settings.RunTime);
            Assert.Equal(DailyReportAutomationSettings.DefaultTargetUrl, settings.TargetUrl);
            Assert.Equal(DailyReportAutomationSettings.DefaultProjectName, settings.ProjectName);
            Assert.Equal(DailyReportAutomationSettings.DefaultTaskName, settings.TaskName);
            Assert.Equal(DailyReportAutomationSettings.DefaultTaskType, settings.TaskType);
            Assert.Equal(100, settings.WorkloadPercent);
            Assert.Contains(Path.Combine("browser-profile", "daily-report"), settings.BrowserProfileDirectory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_round_trip_daily_report_task()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new AppSettingsStore(new AppDataPaths(root));
            await store.SaveDailyReportAutomationSettingsAsync(new DailyReportAutomationSettings
            {
                IsEnabled = true,
                RunTime = new TimeOnly(18, 5),
                TargetUrl = "http://example.test/daily",
                BrowserSessionName = "custom-session",
                BrowserProfileDirectory = Path.Combine(root, "profile"),
                ProjectName = "项目A",
                TaskName = "任务B",
                TaskType = "测试",
                WorkloadPercent = 80
            });

            var settings = await store.LoadDailyReportAutomationSettingsAsync();

            Assert.True(settings.IsEnabled);
            Assert.Equal(new TimeOnly(18, 5), settings.RunTime);
            Assert.Equal("http://example.test/daily", settings.TargetUrl);
            Assert.Equal("custom-session", settings.BrowserSessionName);
            Assert.Equal(Path.Combine(root, "profile"), settings.BrowserProfileDirectory);
            Assert.Equal("项目A", settings.ProjectName);
            Assert.Equal("任务B", settings.TaskName);
            Assert.Equal("测试", settings.TaskType);
            Assert.Equal(80, settings.WorkloadPercent);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Scheduler_triggers_once_at_configured_time()
    {
        var store = new InMemorySettingsStore(new DailyReportAutomationSettings
        {
            IsEnabled = true,
            RunTime = new TimeOnly(17, 50)
        });
        var time = new FakeTimeProvider(new DateTime(2026, 7, 1, 17, 49, 59));
        var count = 0;
        var scheduler = new DailyReportAutomationScheduler(store, time, () =>
        {
            count++;
            return Task.CompletedTask;
        });

        await scheduler.ReloadAsync();
        await InvokeCheckAsync(scheduler);
        time.Now = new DateTime(2026, 7, 1, 17, 50, 0);
        await InvokeCheckAsync(scheduler);
        await InvokeCheckAsync(scheduler);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Scheduler_does_not_catch_up_after_configured_time_window()
    {
        var store = new InMemorySettingsStore(new DailyReportAutomationSettings
        {
            IsEnabled = true,
            RunTime = new TimeOnly(17, 50)
        });
        var time = new FakeTimeProvider(new DateTime(2026, 7, 1, 17, 51, 0));
        var count = 0;
        var scheduler = new DailyReportAutomationScheduler(store, time, () =>
        {
            count++;
            return Task.CompletedTask;
        });

        await scheduler.ReloadAsync();
        await InvokeCheckAsync(scheduler);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Playwright_login_open_arguments_use_visible_edge_browser()
    {
        var settings = new DailyReportAutomationSettings
        {
            TargetUrl = "http://example.test",
            BrowserProfileDirectory = "C:\\temp\\profile"
        };

        var arguments = PlaywrightCliAutomationRunner.CreateOpenArguments(settings);

        Assert.Contains("--headed", arguments);
        Assert.Contains("--browser=msedge", arguments);
        Assert.Contains("--profile=C:\\temp\\profile", arguments);
        Assert.Equal("http://example.test", arguments.Last());
    }

    [Fact]
    public void Playwright_automation_open_arguments_use_headless_edge_browser()
    {
        var settings = new DailyReportAutomationSettings
        {
            BrowserSessionName = "daily-session",
            TargetUrl = "http://example.test",
            BrowserProfileDirectory = "C:\\temp\\profile"
        };

        var arguments = PlaywrightCliAutomationRunner.CreateAutomationOpenArguments(settings);
        var sessionName = PlaywrightCliAutomationRunner.CreateAutomationSessionName(settings);

        Assert.DoesNotContain("--headed", arguments);
        Assert.Contains("--browser=msedge", arguments);
        Assert.Contains("--profile=C:\\temp\\profile", arguments);
        Assert.Equal("http://example.test", arguments.Last());
        Assert.Equal("daily-session-headless", sessionName);
    }

    [Fact]
    public void Playwright_cli_prefers_installed_command()
    {
        var root = CreateTempDirectory();
        try
        {
            var cliPath = Path.Combine(root, "playwright-cli.cmd");
            var npxPath = Path.Combine(root, "npx.cmd");
            File.WriteAllText(cliPath, "@echo off");
            File.WriteAllText(npxPath, "@echo off");

            var invocation = PlaywrightCliAutomationRunner.ResolveCliInvocation([root]);

            Assert.True(invocation.UsesCommandShell);
            Assert.Equal(cliPath, invocation.CommandPath);
            Assert.Contains("/c", invocation.PrefixArguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Playwright_cli_falls_back_to_official_npm_package()
    {
        var root = CreateTempDirectory();
        try
        {
            var npxPath = Path.Combine(root, "npx.cmd");
            File.WriteAllText(npxPath, "@echo off");

            var invocation = PlaywrightCliAutomationRunner.ResolveCliInvocation([root]);

            Assert.True(invocation.UsesCommandShell);
            Assert.Equal(npxPath, invocation.CommandPath);
            Assert.Contains("/c", invocation.PrefixArguments);
            Assert.Equal(["--yes", "--package=@playwright/cli", "playwright-cli"], invocation.CommandPrefixArguments);
            Assert.DoesNotContain("\\\\", invocation.CommandPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Windows_command_line_preserves_spaces_in_batch_file_path()
    {
        var root = CreateTempDirectory();
        try
        {
            var scriptDirectory = Path.Combine(root, "cli folder");
            Directory.CreateDirectory(scriptDirectory);
            var scriptPath = Path.Combine(scriptDirectory, "echo-cli.cmd");
            File.WriteAllText(scriptPath, "@echo off\r\necho %~1\r\n");
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var commandLine = PlaywrightCliAutomationRunner.BuildWindowsCommandLine(scriptPath, ["hello"]);
            startInfo.Arguments = $"/d /s /c \"{commandLine}\"";

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("cmd.exe did not start.");
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.True(process.ExitCode == 0, error);
            Assert.Equal("hello", output.Trim());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Playwright_automation_closes_login_session_before_opening_shared_profile()
    {
        var root = CreateTempDirectory();
        try
        {
            var settings = new DailyReportAutomationSettings
            {
                BrowserSessionName = "daily-session",
                TargetUrl = "http://example.test/sys/daily",
                BrowserProfileDirectory = Path.Combine(root, "profile")
            };
            var cli = new FakePlaywrightCli();
            cli.AddOpenSession(settings.BrowserSessionName, headed: true);
            var runner = new PlaywrightCliAutomationRunner(new AppDataPaths(root), cli.ExecuteAsync);
            var screenshotPath = Path.Combine(root, "before-submit.png");

            await runner.FillDailyReportAsync(settings, "完成自动化修复", screenshotPath);

            var closeIndex = cli.Commands.FindIndex(command =>
                command.SessionName == settings.BrowserSessionName
                && command.Arguments.FirstOrDefault() == "close");
            var openIndex = cli.Commands.FindIndex(command =>
                command.SessionName == PlaywrightCliAutomationRunner.CreateAutomationSessionName(settings)
                && command.Arguments.FirstOrDefault() == "open");
            Assert.True(closeIndex >= 0, "The login session should be closed before automation starts.");
            Assert.True(openIndex > closeIndex, "The headless session must open after releasing the shared profile.");
            Assert.True(File.Exists(screenshotPath));
            Assert.False(cli.IsSessionOpen(PlaywrightCliAutomationRunner.CreateAutomationSessionName(settings)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Playwright_submit_recovers_missing_session_and_refills_form_before_clicking_submit()
    {
        var root = CreateTempDirectory();
        try
        {
            var settings = new DailyReportAutomationSettings
            {
                BrowserSessionName = "daily-session",
                TargetUrl = "http://example.test/sys/daily",
                BrowserProfileDirectory = Path.Combine(root, "profile")
            };
            var cli = new FakePlaywrightCli();
            var runner = new PlaywrightCliAutomationRunner(new AppDataPaths(root), cli.ExecuteAsync);
            var screenshotPath = Path.Combine(root, "after-submit.png");

            await runner.SubmitDailyReportAsync(settings, "确认后的日报文本", screenshotPath);

            var scripts = cli.ExecutedScripts;
            Assert.Equal(2, scripts.Count);
            Assert.Contains("chooseOption", scripts[0]);
            Assert.Contains("submit.click", scripts[1]);
            Assert.True(File.Exists(screenshotPath));
            Assert.False(cli.IsSessionOpen(PlaywrightCliAutomationRunner.CreateAutomationSessionName(settings)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Playwright_submit_closes_headless_session_when_automation_fails()
    {
        var root = CreateTempDirectory();
        try
        {
            var settings = new DailyReportAutomationSettings
            {
                BrowserSessionName = "daily-session",
                TargetUrl = "http://example.test/sys/daily",
                BrowserProfileDirectory = Path.Combine(root, "profile")
            };
            var cli = new FakePlaywrightCli
            {
                RunCodeException = new InvalidOperationException("Simulated automation failure.")
            };
            var runner = new PlaywrightCliAutomationRunner(new AppDataPaths(root), cli.ExecuteAsync);
            var screenshotPath = Path.Combine(root, "after-submit.png");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.SubmitDailyReportAsync(settings, "确认后的日报文本", screenshotPath));

            Assert.False(cli.IsSessionOpen(PlaywrightCliAutomationRunner.CreateAutomationSessionName(settings)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Playwright_session_parser_keeps_nested_properties_and_stops_at_next_session()
    {
        var output = """
            ### Browsers
            - daily-session-headless:
              - status: open
              - browser-type: msedge
              - headed: false
            - another-session:
              - status: open
              - browser-type: chromium
              - headed: true
            """;

        var session = PlaywrightCliAutomationRunner.FindSessionBlock(output, "daily-session-headless");

        Assert.NotNull(session);
        Assert.Contains("status: open", session);
        Assert.Contains("headed: false", session);
        Assert.DoesNotContain("another-session", session);
        Assert.DoesNotContain("chromium", session);
    }

    private static Task InvokeCheckAsync(DailyReportAutomationScheduler scheduler)
    {
        var method = typeof(DailyReportAutomationScheduler).GetMethod("CheckAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CheckAsync not found.");
        return (Task)method.Invoke(scheduler, null)!;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Timenotifier.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeTimeProvider(DateTime now) : ITimeProvider
    {
        public DateTime Now { get; set; } = now;
    }

    private sealed class InMemorySettingsStore(DailyReportAutomationSettings automationSettings) : IAppSettingsStore
    {
        public Task<ModelProviderSettings> LoadModelSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelProviderSettings());
        }

        public Task SaveModelSettingsAsync(ModelProviderSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<ReminderSettings> LoadReminderSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DefaultReminders.CreateSettings());
        }

        public Task SaveReminderSettingsAsync(ReminderSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ReportTemplate>> LoadReportTemplatesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ReportTemplate>>(AppSettingsStore.CreateDefaultReportTemplates());
        }

        public Task SaveReportTemplatesAsync(IReadOnlyList<ReportTemplate> templates, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<EvidenceCaptureSettings> LoadEvidenceCaptureSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EvidenceCaptureSettings());
        }

        public Task SaveEvidenceCaptureSettingsAsync(EvidenceCaptureSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<DailyReportAutomationSettings> LoadDailyReportAutomationSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(automationSettings);
        }

        public Task SaveDailyReportAutomationSettingsAsync(DailyReportAutomationSettings settings, CancellationToken cancellationToken = default)
        {
            automationSettings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlaywrightCli
    {
        private readonly Dictionary<string, bool> _openSessions = new(StringComparer.OrdinalIgnoreCase);

        public List<(string SessionName, IReadOnlyList<string> Arguments)> Commands { get; } = [];
        public List<string> ExecutedScripts { get; } = [];
        public Exception? RunCodeException { get; init; }

        public void AddOpenSession(string sessionName, bool headed)
        {
            _openSessions[sessionName] = headed;
        }

        public bool IsSessionOpen(string sessionName)
        {
            return _openSessions.ContainsKey(sessionName);
        }

        public Task<string> ExecuteAsync(
            string sessionName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Commands.Add((sessionName, arguments.ToArray()));
            if (arguments.FirstOrDefault() == "list")
            {
                var sessions = string.Join(
                    Environment.NewLine,
                    _openSessions.Select(session =>
                        $"- {session.Key}:{Environment.NewLine}  - status: open{Environment.NewLine}  - browser-type: msedge{Environment.NewLine}  - headed: {session.Value.ToString().ToLowerInvariant()}"));
                return Task.FromResult($"### Browsers{Environment.NewLine}{sessions}");
            }

            if (arguments.FirstOrDefault() == "close")
            {
                _openSessions.Remove(sessionName);
                return Task.FromResult("");
            }

            if (arguments.FirstOrDefault() == "open")
            {
                if (_openSessions.Keys.Any(openSession => !string.Equals(openSession, sessionName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("Browser is already in use for the shared profile.");
                }

                _openSessions[sessionName] = arguments.Contains("--headed");
                return Task.FromResult("");
            }

            if (arguments.Contains("run-code"))
            {
                if (RunCodeException is not null)
                {
                    throw RunCodeException;
                }

                var fileArgument = arguments.Single(argument => argument.StartsWith("--filename=", StringComparison.Ordinal));
                var script = File.ReadAllText(fileArgument["--filename=".Length..]);
                ExecutedScripts.Add(script);
                CreateScreenshotFromScript(script);
                return Task.FromResult("");
            }

            throw new InvalidOperationException($"Unexpected command: {string.Join(' ', arguments)}");
        }

        private static void CreateScreenshotFromScript(string script)
        {
            var match = Regex.Match(script, @"const cfg = (?<json>\{[^\r\n]+\});");
            Assert.True(match.Success, "The generated automation script should contain a JSON payload.");
            using var payload = JsonDocument.Parse(match.Groups["json"].Value);
            var screenshotPath = payload.RootElement.GetProperty("screenshotPath").GetString()
                ?? throw new InvalidOperationException("screenshotPath is missing.");
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
            File.WriteAllBytes(screenshotPath, [1]);
        }
    }

}
