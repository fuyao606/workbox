using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Timenotifier.Models;
using Timenotifier.Services.Infrastructure;

namespace Timenotifier.Services.Automation;

public interface IPlaywrightCliAutomationRunner
{
    Task OpenLoginBrowserAsync(DailyReportAutomationSettings settings, CancellationToken cancellationToken = default);
    Task FillDailyReportAsync(DailyReportAutomationSettings settings, string dailyText, string screenshotPath, CancellationToken cancellationToken = default);
    Task SubmitDailyReportAsync(DailyReportAutomationSettings settings, string dailyText, string screenshotPath, CancellationToken cancellationToken = default);
}

internal delegate Task<string> PlaywrightCliCommandExecutor(
    string sessionName,
    IReadOnlyList<string> arguments,
    TimeSpan timeout,
    CancellationToken cancellationToken);

internal sealed record PlaywrightCliInvocation(
    string FileName,
    IReadOnlyList<string> PrefixArguments,
    bool UsesCommandShell,
    string? CommandPath,
    IReadOnlyList<string>? CommandPrefixArguments = null);

public sealed class PlaywrightCliAutomationRunner : IPlaywrightCliAutomationRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AppDataPaths _paths;
    private readonly PlaywrightCliCommandExecutor? _commandExecutor;
    private readonly SemaphoreSlim _automationGate = new(1, 1);

    public PlaywrightCliAutomationRunner(AppDataPaths paths)
    {
        _paths = paths;
    }

    internal PlaywrightCliAutomationRunner(AppDataPaths paths, PlaywrightCliCommandExecutor commandExecutor)
    {
        _paths = paths;
        _commandExecutor = commandExecutor;
    }

    public Task OpenLoginBrowserAsync(DailyReportAutomationSettings settings, CancellationToken cancellationToken = default)
    {
        return RunExclusiveAsync("打开登录浏览器", cancellationToken, async () =>
        {
            Directory.CreateDirectory(settings.BrowserProfileDirectory);
            var loginSession = await GetSessionBlockAsync(settings, settings.BrowserSessionName, cancellationToken);
            if (IsOpenSession(loginSession, headed: true))
            {
                await RunCliAsync(settings, settings.BrowserSessionName, ["goto", settings.TargetUrl], TimeSpan.FromSeconds(45), "跳转至日报登录页", cancellationToken);
                return;
            }

            await CloseSessionIfPresentAsync(settings, CreateAutomationSessionName(settings), cancellationToken);
            await CloseSessionIfPresentAsync(settings, settings.BrowserSessionName, cancellationToken);
            await RunCliAsync(settings, settings.BrowserSessionName, CreateOpenArguments(settings), TimeSpan.FromSeconds(45), "启动日报登录浏览器", cancellationToken);
            var listOutput = await ListSessionsAsync(settings, settings.BrowserSessionName, cancellationToken);
            var session = FindSessionBlock(listOutput, settings.BrowserSessionName);
            if (!IsOpenSession(session, headed: true))
            {
                throw new InvalidOperationException($"playwright-cli 已返回成功，但没有检测到可见浏览器会话。输出：{listOutput}");
            }
        });
    }

    public static IReadOnlyList<string> CreateOpenArguments(DailyReportAutomationSettings settings)
    {
        return [
            "open",
            "--headed",
            "--browser=msedge",
            $"--profile={settings.BrowserProfileDirectory}",
            settings.TargetUrl
        ];
    }

    public static string CreateAutomationSessionName(DailyReportAutomationSettings settings)
    {
        return $"{settings.BrowserSessionName}-headless";
    }

    public static IReadOnlyList<string> CreateAutomationOpenArguments(DailyReportAutomationSettings settings)
    {
        return [
            "open",
            "--browser=msedge",
            $"--profile={settings.BrowserProfileDirectory}",
            settings.TargetUrl
        ];
    }

    public Task FillDailyReportAsync(
        DailyReportAutomationSettings settings,
        string dailyText,
        string screenshotPath,
        CancellationToken cancellationToken = default)
    {
        return RunExclusiveAsync("填写日报", cancellationToken, async () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
            TryDelete(screenshotPath);
            Exception? operationException = null;
            try
            {
                await EnsureAutomationBrowserAsync(settings, cancellationToken);
                await FillFormAsync(settings, dailyText, screenshotPath, cancellationToken);
                EnsureScreenshotCreated(screenshotPath, "提交前确认截图");
            }
            catch (Exception ex)
            {
                operationException = ex;
                throw;
            }
            finally
            {
                await CloseAutomationSessionSafelyAsync(settings, operationException);
            }
        });
    }

    public Task SubmitDailyReportAsync(
        DailyReportAutomationSettings settings,
        string dailyText,
        string screenshotPath,
        CancellationToken cancellationToken = default)
    {
        return RunExclusiveAsync("提交日报", cancellationToken, async () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
            TryDelete(screenshotPath);
            Exception? operationException = null;
            try
            {
                await EnsureAutomationBrowserAsync(settings, cancellationToken);
                var recoveryScreenshotPath = Path.Combine(
                    Path.GetDirectoryName(screenshotPath)!,
                    $".before-submit-recovery-{Guid.NewGuid():N}.png");
                try
                {
                    await FillFormAsync(settings, dailyText, recoveryScreenshotPath, cancellationToken);
                    EnsureScreenshotCreated(recoveryScreenshotPath, "提交前会话恢复截图");
                }
                finally
                {
                    TryDelete(recoveryScreenshotPath);
                }

                var payload = new
                {
                    dailyText,
                    screenshotPath,
                    projectName = settings.ProjectName,
                    taskName = settings.TaskName,
                    taskType = settings.TaskType,
                    workloadPercent = settings.WorkloadPercent.ToString()
                };
                await RunCodeAsync(settings, CreateAutomationSessionName(settings), BuildSubmitScript(payload), TimeSpan.FromSeconds(60), "确认并提交日报", cancellationToken);
                EnsureScreenshotCreated(screenshotPath, "提交后确认截图");
            }
            catch (Exception ex)
            {
                operationException = ex;
                throw;
            }
            finally
            {
                await CloseAutomationSessionSafelyAsync(settings, operationException);
            }
        });
    }

    private async Task FillFormAsync(
        DailyReportAutomationSettings settings,
        string dailyText,
        string screenshotPath,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            url = settings.TargetUrl,
            projectName = settings.ProjectName,
            taskName = settings.TaskName,
            taskType = settings.TaskType,
            workloadPercent = settings.WorkloadPercent.ToString(),
            dailyText,
            screenshotPath
        };
        await RunCodeAsync(
            settings,
            CreateAutomationSessionName(settings),
            BuildFillScript(payload),
            TimeSpan.FromMinutes(2),
            "填写日报表单并截图",
            cancellationToken);
    }

    private async Task EnsureAutomationBrowserAsync(DailyReportAutomationSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(settings.BrowserProfileDirectory);
        var automationSessionName = CreateAutomationSessionName(settings);
        var existingSession = await GetSessionBlockAsync(settings, automationSessionName, cancellationToken);
        if (IsOpenSession(existingSession, headed: false))
        {
            return;
        }

        await CloseSessionIfPresentAsync(settings, automationSessionName, cancellationToken);
        await CloseSessionIfPresentAsync(settings, settings.BrowserSessionName, cancellationToken);
        await RunCliAsync(settings, automationSessionName, CreateAutomationOpenArguments(settings), TimeSpan.FromSeconds(45), "启动无头日报浏览器", cancellationToken);
        var listOutput = await ListSessionsAsync(settings, automationSessionName, cancellationToken);
        var session = FindSessionBlock(listOutput, automationSessionName);
        if (!IsOpenSession(session, headed: false))
        {
            throw new InvalidOperationException($"playwright-cli 已返回成功，但没有检测到无头自动化浏览器会话。输出：{listOutput}");
        }
    }

    private async Task RunExclusiveAsync(
        string operationName,
        CancellationToken cancellationToken,
        Func<Task> operation)
    {
        await _automationGate.WaitAsync(cancellationToken);
        try
        {
            await operation();
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{operationName}超时。{ex.Message}", ex);
        }
        finally
        {
            _automationGate.Release();
        }
    }

    private async Task<string?> GetSessionBlockAsync(
        DailyReportAutomationSettings settings,
        string sessionName,
        CancellationToken cancellationToken)
    {
        var listOutput = await ListSessionsAsync(settings, sessionName, cancellationToken);
        return FindSessionBlock(listOutput, sessionName);
    }

    private Task<string> ListSessionsAsync(
        DailyReportAutomationSettings settings,
        string sessionName,
        CancellationToken cancellationToken)
    {
        return RunCliAsync(settings, sessionName, ["list"], TimeSpan.FromSeconds(15), "检查浏览器会话", cancellationToken);
    }

    private async Task CloseSessionIfPresentAsync(
        DailyReportAutomationSettings settings,
        string sessionName,
        CancellationToken cancellationToken)
    {
        if (await GetSessionBlockAsync(settings, sessionName, cancellationToken) is not null)
        {
            await RunCliAsync(settings, sessionName, ["close"], TimeSpan.FromSeconds(15), cancellationToken);
        }
    }

    private async Task CloseAutomationSessionSafelyAsync(
        DailyReportAutomationSettings settings,
        Exception? operationException)
    {
        try
        {
            await CloseSessionIfPresentAsync(
                settings,
                CreateAutomationSessionName(settings),
                CancellationToken.None);
        }
        catch when (operationException is not null)
        {
            // Preserve the primary automation error when cleanup also fails.
        }
    }

    private static bool IsOpenSession(string? sessionBlock, bool headed)
    {
        return sessionBlock is not null
            && sessionBlock.Contains("status: open", StringComparison.OrdinalIgnoreCase)
            && sessionBlock.Contains($"headed: {headed.ToString().ToLowerInvariant()}", StringComparison.OrdinalIgnoreCase);
    }

    private Task RunCodeAsync(
        DailyReportAutomationSettings settings,
        string sessionName,
        string script,
        TimeSpan timeout,
        string operationName,
        CancellationToken cancellationToken)
    {
        return RunCodeAsync(settings, sessionName, script, timeout, cancellationToken);
    }

    private async Task RunCodeAsync(
        DailyReportAutomationSettings settings,
        string sessionName,
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        var scriptPath = Path.Combine(_paths.RootDirectory, $"daily-report-automation-{Guid.NewGuid():N}.js");
        await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8, cancellationToken);
        try
        {
            await RunCliAsync(settings, sessionName, ["--raw", "run-code", $"--filename={scriptPath}"], timeout, cancellationToken);
        }
        finally
        {
            TryDelete(scriptPath);
        }
    }

    private Task<string> RunCliAsync(
        DailyReportAutomationSettings settings,
        string sessionName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string operationName,
        CancellationToken cancellationToken)
    {
        return RunCliAsync(settings, sessionName, arguments, timeout, cancellationToken);
    }

    private async Task<string> RunCliAsync(
        DailyReportAutomationSettings settings,
        string sessionName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_commandExecutor is not null)
        {
            return await _commandExecutor(sessionName, arguments, timeout, cancellationToken);
        }

        var invocation = ResolveCliInvocation(GetCliSearchDirectories());
        var commandArguments = new List<string>(arguments.Count + 1)
        {
            $"-s={sessionName}"
        };
        commandArguments.AddRange(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (invocation.UsesCommandShell)
        {
            var commandLine = BuildWindowsCommandLine(
                invocation.CommandPath!,
                (invocation.CommandPrefixArguments ?? []).Concat(commandArguments).ToArray());
            startInfo.Arguments = $"{string.Join(" ", invocation.PrefixArguments)} \"{commandLine}\"";
        }
        else
        {
            foreach (var prefixArgument in invocation.PrefixArguments)
            {
                startInfo.ArgumentList.Add(prefixArgument);
            }

            foreach (var argument in commandArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                "未找到可用的 Playwright CLI。请安装 Node.js，并运行 npm install --global @playwright/cli 后重试。",
                ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(ToFriendlyError(output, error));
            }

            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("playwright-cli 执行超时，请检查浏览器页面是否卡住或需要登录。");
        }
    }

    internal static PlaywrightCliInvocation ResolveCliInvocation(IEnumerable<string> searchDirectories)
    {
        var directories = searchDirectories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Path.GetFullPath(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var cliPath = FindExecutable(directories, "playwright-cli.cmd")
                ?? FindExecutable(directories, "playwright-cli.exe");
            if (cliPath is not null)
            {
                var commandShell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                return new PlaywrightCliInvocation(
                    File.Exists(commandShell) ? commandShell : "cmd.exe",
                    ["/d", "/s", "/c"],
                    UsesCommandShell: true,
                    cliPath);
            }

            var npxPath = FindExecutable(directories, "npx.cmd")
                ?? FindExecutable(directories, "npx.exe");
            if (npxPath is not null)
            {
                var commandShell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                return new PlaywrightCliInvocation(
                    File.Exists(commandShell) ? commandShell : "cmd.exe",
                    ["/d", "/s", "/c"],
                    UsesCommandShell: true,
                    npxPath,
                    ["--yes", "--package=@playwright/cli", "playwright-cli"]);
            }
        }
        else if (FindExecutable(directories, "playwright-cli") is { } unixCliPath)
        {
            return new PlaywrightCliInvocation(unixCliPath, [], UsesCommandShell: false, CommandPath: null);
        }

        throw new InvalidOperationException(
            "未找到可用的 Playwright CLI 或 npx。请先安装 Node.js，并运行 npm install --global @playwright/cli 后重试。");
    }

    private static IEnumerable<string> GetCliSearchDirectories()
    {
        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var knownDirectories = new List<string>(pathDirectories)
        {
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs")
        };
        return knownDirectories;
    }

    private static string? FindExecutable(IEnumerable<string> directories, string fileName)
    {
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static string BuildWindowsCommandLine(string commandPath, IReadOnlyList<string> arguments)
    {
        return string.Join(
            " ",
            new[] { commandPath }.Concat(arguments).Select(QuoteWindowsArgument));
    }

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0 && argument.All(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return argument;
        }

        return $"\"{argument.Replace("\"", "\\\"")}\"";
    }

    internal static string? FindSessionBlock(string listOutput, string sessionName)
    {
        var lines = listOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        var inBlock = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (inBlock)
                {
                    break;
                }

                inBlock = line.StartsWith($"- {sessionName}:", StringComparison.OrdinalIgnoreCase);
            }

            if (inBlock)
            {
                builder.AppendLine(line);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static void EnsureScreenshotCreated(string screenshotPath, string label)
    {
        if (!File.Exists(screenshotPath))
        {
            throw new InvalidOperationException($"{label}生成失败：截图文件不存在：{screenshotPath}");
        }

        var length = new FileInfo(screenshotPath).Length;
        if (length <= 0)
        {
            throw new InvalidOperationException($"{label}生成失败：截图文件为空：{screenshotPath}");
        }
    }

    private static string ToFriendlyError(string output, string error)
    {
        var raw = $"{output}{Environment.NewLine}{error}".Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "playwright-cli 执行失败，但没有返回错误信息。";
        }

        if (raw.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return $"页面操作超时，请检查是否已登录或页面是否加载完成。{Environment.NewLine}{raw}";
        }

        if (raw.Contains("Browser is already in use", StringComparison.OrdinalIgnoreCase))
        {
            return $"浏览器配置目录正在被其他会话占用，请关闭日报登录浏览器后重试。{Environment.NewLine}{raw}";
        }

        if (raw.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("No node found", StringComparison.OrdinalIgnoreCase))
        {
            return $"页面元素未找到，请确认日报页面结构和选项文本是否变化。{Environment.NewLine}{raw}";
        }

        return raw;
    }

    private static string BuildFillScript(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return $$"""
            async page => {
              const cfg = {{json}};
              const wait = ms => page.waitForTimeout(ms);
              const clean = value => String(value || '').replace(/\s+/g, ' ').trim();
              const normalize = value => clean(value).replace(/，/g, ',');
              const assertContains = (label, actual, expected) => {
                const actualText = normalize(actual);
                const expectedText = normalize(expected);
                if (!actualText.includes(expectedText)) {
                  throw new Error(`${label}未填写成功，当前值：${actual || '(空)'}，期望：${expected}`);
                }
              };
              const selectInput = index => page.getByRole('textbox', { name: '请选择' }).nth(index);
              const projectInput = () => selectInput(1);
              const taskInput = () => selectInput(2);
              const typeInput = () => selectInput(3);
              const workloadInput = () => page.getByRole('spinbutton', { name: '必填' }).first();
              const contentInput = () => page.getByRole('textbox', { name: '请输入内容' }).first();
              const fieldSnapshot = async () => {
                const read = async locator => await locator.count() > 0 ? clean(await locator.inputValue().catch(() => '')) : '(未找到)';
                return [
                  `参与项目名称=${await read(projectInput()) || '(空)'}`,
                  `任务名称=${await read(taskInput()) || '(空)'}`,
                  `任务类型=${await read(typeInput()) || '(空)'}`,
                  `工作量占比=${await read(workloadInput()) || '(空)'}`,
                  `工作内容简述=${await read(contentInput()) || '(空)'}`
                ].join('；');
              };
              const visibleOptions = async () => page.evaluate(() => [...document.querySelectorAll('.el-select-dropdown__item, .ant-select-item-option, .ivu-select-item, li')]
                .filter(el => {
                  const style = window.getComputedStyle(el);
                  const box = el.getBoundingClientRect();
                  return style.visibility !== 'hidden' && style.display !== 'none' && box.width > 0 && box.height > 0;
                })
                .map(el => String(el.textContent || '').replace(/\s+/g, ' ').trim())
                .filter(Boolean)
                .slice(0, 12)
                .join(' | '));
              const fail = async (step, error) => {
                throw new Error(`日报自动化在“${step}”失败：${error.message}。当前字段：${await fieldSnapshot()}。可见选项：${await visibleOptions() || '(无)'}`);
              };
              const optionLocator = optionText => {
                const expectedText = normalize(optionText);
                return page.getByRole('listitem')
                  .filter({ hasText: expectedText })
                  .first();
              };
              const chooseOption = async (label, target, optionText) => {
                await target.scrollIntoViewIfNeeded();
                await wait(200);
                await target.click();
                await wait(700);
                const option = optionLocator(optionText);
                if (await option.count() === 0) {
                  await target.fill(optionText);
                  await wait(700);
                }
                if (await optionLocator(optionText).count() === 0) throw new Error(`未找到${label}下拉选项：${optionText}`);
                await optionLocator(optionText).click();
                await wait(1000);
                assertContains(label, clean(await target.inputValue()), optionText);
              };
              const fillInput = async (label, target, value) => {
                await target.scrollIntoViewIfNeeded();
                await wait(200);
                await target.click();
                await target.fill('');
                await page.keyboard.press('Control+A');
                await page.keyboard.type(String(value), { delay: 10 });
                await wait(700);
                assertContains(label, clean(await target.inputValue()), String(value));
              };
              const capture = async () => {
                await contentInput().scrollIntoViewIfNeeded();
                await wait(500);
                await page.screenshot({ path: cfg.screenshotPath, fullPage: true });
              };
              try {
                const currentUrl = page.url();
                if (!currentUrl.includes('/sys/selfDailyManage') && !currentUrl.includes('/sys/daily')) {
                  await page.goto(cfg.url, { waitUntil: 'domcontentloaded' });
                  await wait(1500);
                }
                const bodyText = await page.locator('body').innerText();
                if (!bodyText.includes('日报')) throw new Error('当前页面不像日报页面，可能尚未登录。');
                const fillButton = page.getByText('填写日报', { exact: true }).first();
                if (await fillButton.count() > 0 && await fillButton.isVisible()) {
                  await fillButton.click();
                  await wait(1600);
                }
                await page.getByText('参与项目明细').first().waitFor({ state: 'visible', timeout: 10000 });
                await chooseOption('参与项目名称', projectInput(), cfg.projectName);
                await chooseOption('任务名称', taskInput(), cfg.taskName);
                await chooseOption('任务类型', typeInput(), cfg.taskType);
                await fillInput('工作量占比', workloadInput(), cfg.workloadPercent);
                const textarea = contentInput();
                await textarea.fill(cfg.dailyText);
                assertContains('工作内容简述', clean(await textarea.inputValue()), cfg.dailyText.slice(0, Math.min(40, cfg.dailyText.length)));
                await wait(800);
                await capture();
              } catch (error) {
                await fail('填报', error);
              }
            }
            """;
    }

    private static string BuildSubmitScript(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return $$"""
            async page => {
              const wait = ms => page.waitForTimeout(ms);
              const clean = value => String(value || '').replace(/\s+/g, ' ').trim();
              const cfg = {{json}};
              const normalize = value => clean(value).replace(/，/g, ',');
              const selectInput = index => page.getByRole('textbox', { name: '请选择' }).nth(index);
              const projectInput = () => selectInput(1);
              const taskInput = () => selectInput(2);
              const typeInput = () => selectInput(3);
              const workloadInput = () => page.getByRole('spinbutton', { name: '必填' }).first();
              const contentInput = () => page.getByRole('textbox', { name: '请输入内容' }).first();
              const requireFilled = async (label, locator, expected) => {
                const actualValue = clean(await locator.inputValue());
                const actual = normalize(actualValue);
                const expectedText = normalize(expected);
                if (!actual.includes(expectedText)) {
                  throw new Error(`${label}未填写成功，当前值：${actualValue || '(空)'}，期望：${expected}`);
                }
              };
              const textarea = contentInput();
              await textarea.fill(cfg.dailyText);
              await textarea.scrollIntoViewIfNeeded();
              await wait(400);
              await requireFilled('参与项目名称', projectInput(), cfg.projectName);
              await requireFilled('任务名称', taskInput(), cfg.taskName);
              await requireFilled('任务类型', typeInput(), cfg.taskType);
              await requireFilled('工作量占比', workloadInput(), cfg.workloadPercent);
              await requireFilled('工作内容简述', textarea, cfg.dailyText.slice(0, Math.min(40, cfg.dailyText.length)));
              const submit = page.getByRole('button', { name: '提交' }).last();
              if (await submit.count() === 0) throw new Error('未找到“提交”按钮。');
              await submit.scrollIntoViewIfNeeded();
              await submit.click();
              await wait(900);
              const confirm = page.getByRole('button', { name: /^(确定|确认)$/ }).last();
              if (await confirm.count() > 0 && await confirm.isVisible()) {
                await confirm.click();
                await wait(2500);
              } else {
                await wait(2200);
              }
              await page.screenshot({ path: cfg.screenshotPath, fullPage: true });
            }
            """;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
