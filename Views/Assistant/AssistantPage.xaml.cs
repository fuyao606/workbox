using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Timenotifier.Models;
using Timenotifier.Services.Assistant;

namespace Timenotifier.Views.Assistant;

public partial class AssistantPage : System.Windows.Controls.UserControl
{
    private readonly IAssistantConversationService _conversationService;
    private readonly IAssistantSessionStore _sessionStore;
    private readonly IAssistantAttachmentService _attachmentService;
    private readonly Func<AssistantActionExecutionResult, Task> _executeHostResultAsync;
    private readonly ObservableCollection<AssistantSession> _sessions = [];
    private readonly List<PreparedAssistantAttachment> _attachments = [];
    private AssistantSession? _currentSession;
    private AssistantActionProposal? _pendingProposal;
    private CancellationTokenSource? _requestCancellation;
    private bool _initialized;

    public AssistantPage(
        IAssistantConversationService conversationService,
        IAssistantSessionStore sessionStore,
        IAssistantAttachmentService attachmentService,
        Func<AssistantActionExecutionResult, Task> executeHostResultAsync)
    {
        _conversationService = conversationService;
        _sessionStore = sessionStore;
        _attachmentService = attachmentService;
        _executeHostResultAsync = executeHostResultAsync;
        InitializeComponent();

        SessionList.ItemsSource = _sessions;
        ModuleCombo.ItemsSource = CreateModuleChoices();
        ModuleCombo.SelectedIndex = 0;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        var transient = _conversationService.CreateTransientSession();
        AddUiLoopFormattedFixture(transient);
        _sessions.Add(transient);
        _currentSession = transient;
        SessionList.SelectedItem = transient;
        RenderMessages();

        try
        {
            var loaded = await _conversationService.LoadSessionsAsync();
            foreach (var session in loaded.Sessions)
            {
                _sessions.Add(session);
            }
            if (loaded.Warnings.Count > 0)
            {
                SetState(string.Join(" ", loaded.Warnings));
            }
        }
        catch
        {
            SetState("历史会话暂时无法读取，新对话仍可使用。");
        }
    }

    private static void AddUiLoopFormattedFixture(AssistantSession session)
    {
        if (!Environment.GetCommandLineArgs().Any(argument => string.Equals(
                argument,
                "--ui-loop-assistant-fixture=formatted",
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        session.Title = "格式化输出示例";
        session.Messages.Add(new AssistantMessage
        {
            Role = AssistantMessageRole.Assistant,
            CreatedAt = DateTime.Now,
            Text = """
                ### 回顾摘要
                - **范围**：2026-08-04
                - **证据**：3 条，已纳入 2 条
                - **来源**：Codex
                - **工作目录**：D:\Code\workbox
                **目标概览**
                - 将会话记录整理为易读的工作摘要
                **工作结果**
                - 已将输出拆分为清晰条目
                """
        });
    }

    private void OnNewSessionClicked(object sender, RoutedEventArgs e)
    {
        if (_requestCancellation is not null)
        {
            SetState("请先完成或取消当前请求。");
            return;
        }

        var session = _conversationService.CreateTransientSession();
        _sessions.Insert(0, session);
        SessionList.SelectedItem = session;
        ComposerTextBox.Text = "";
        _attachments.Clear();
        RefreshAttachments();
        ComposerTextBox.Focus();
    }

    private async void OnDeleteSessionClicked(object sender, RoutedEventArgs e)
    {
        if (_currentSession is null || _requestCancellation is not null)
        {
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            Window.GetWindow(this),
            $"确定删除对话“{_currentSession.Title}”吗？此操作无法撤销。",
            "删除助手对话",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var deleted = _currentSession;
        try
        {
            await _sessionStore.DeleteAsync(deleted.Id);
            _sessions.Remove(deleted);
            var next = _sessions.FirstOrDefault() ?? _conversationService.CreateTransientSession();
            if (!_sessions.Contains(next))
            {
                _sessions.Add(next);
            }
            SessionList.SelectedItem = next;
            SetState("对话已删除。");
        }
        catch
        {
            SetState("对话删除失败，请稍后重试。");
        }
    }

    private void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not AssistantSession selected)
        {
            return;
        }
        if (_requestCancellation is not null && _currentSession is not null && !ReferenceEquals(selected, _currentSession))
        {
            SessionList.SelectedItem = _currentSession;
            SetState("请求进行中，取消后才能切换对话。");
            return;
        }

        _currentSession = selected;
        _pendingProposal = null;
        HidePendingAction();
        RenderMessages();
    }

    private async void OnAddAttachmentClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择助手附件",
            Multiselect = true,
            Filter = "支持的文件|*.png;*.jpg;*.jpeg;*.webp;*.txt;*.md;*.json;*.csv;*.pdf;*.docx;*.xlsx;*.pptx|所有文件|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            var prepared = new List<PreparedAssistantAttachment>();
            foreach (var file in dialog.FileNames)
            {
                prepared.Add(await _attachmentService.PrepareAsync(file));
            }
            var combined = _attachments.Concat(prepared).ToList();
            _attachmentService.ValidateBatch(combined);
            _attachments.AddRange(prepared);
            RefreshAttachments();
            SetState($"已添加 {prepared.Count} 个附件。");
        }
        catch (AssistantAttachmentException exception)
        {
            SetState(exception.Message);
        }
        catch
        {
            SetState("附件读取失败，文件未加入本次消息。");
        }
    }

    private void OnRemoveAttachmentClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PreparedAssistantAttachment attachment)
        {
            return;
        }
        _attachments.Remove(attachment);
        RefreshAttachments();
    }

    private async void OnSendClicked(object sender, RoutedEventArgs e)
    {
        await SendCurrentAsync();
    }

    private async void OnComposerPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }
        e.Handled = true;
        await SendCurrentAsync();
    }

    private async Task SendCurrentAsync()
    {
        if (_requestCancellation is not null || _currentSession is null)
        {
            return;
        }

        var text = ComposerTextBox.Text.Trim();
        if (text.Length == 0)
        {
            SetState("请输入消息后再发送。");
            ComposerTextBox.Focus();
            return;
        }

        try
        {
            _attachmentService.ValidateBatch(_attachments);
        }
        catch (AssistantAttachmentException exception)
        {
            SetState(exception.Message);
            return;
        }

        _requestCancellation = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var module = (ModuleCombo.SelectedItem as ModuleChoice)?.Module ?? AssistantModule.Assistant;
            var result = await _conversationService.SendAsync(
                new AssistantTurnRequest(_currentSession, text, module, _attachments.ToList()),
                _requestCancellation.Token);
            ComposerTextBox.Text = "";
            _attachments.Clear();
            RefreshAttachments();
            _pendingProposal = result.Proposal;
            RenderMessages();
            if (result.Proposal is not null)
            {
                ShowPendingAction(result.Proposal);
            }
            else
            {
                HidePendingAction();
            }
            if (result.ImmediateActionResult is not null)
            {
                await _executeHostResultAsync(result.ImmediateActionResult);
            }
            SessionList.Items.Refresh();
            SetState(result.Proposal is null ? "回复完成" : "等待确认动作");
        }
        catch (OperationCanceledException)
        {
            RenderMessages();
            SetState("请求已取消，输入和附件已保留，可再次发送。");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("配置", StringComparison.Ordinal)
            || exception.Message.Contains("正在处理", StringComparison.Ordinal)
            || exception.Message.Contains("消息不能为空", StringComparison.Ordinal))
        {
            RenderMessages();
            SetState(exception.Message);
        }
        catch
        {
            RenderMessages();
            SetState("模型请求失败，请检查模型配置或网络后重试。");
        }
        finally
        {
            _requestCancellation?.Dispose();
            _requestCancellation = null;
            SetBusy(false);
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        _requestCancellation?.Cancel();
    }

    private async void OnConfirmActionClicked(object sender, RoutedEventArgs e)
    {
        if (_pendingProposal is null || _currentSession is null)
        {
            return;
        }

        ConfirmActionButton.IsEnabled = false;
        DeclineActionButton.IsEnabled = false;
        try
        {
            var result = await _conversationService.ExecuteAsync(_currentSession, _pendingProposal);
            await _executeHostResultAsync(result);
            SetState(result.Message);
            _pendingProposal = null;
            HidePendingAction();
            RenderMessages();
        }
        catch (AssistantActionException exception)
        {
            SetState(exception.Message);
            _pendingProposal = null;
            HidePendingAction();
            RenderMessages();
        }
        catch
        {
            SetState("动作执行失败，当前设置未被助手继续修改。");
        }
        finally
        {
            ConfirmActionButton.IsEnabled = true;
            DeclineActionButton.IsEnabled = true;
        }
    }

    private async void OnDeclineActionClicked(object sender, RoutedEventArgs e)
    {
        if (_pendingProposal is null || _currentSession is null)
        {
            return;
        }
        try
        {
            await _conversationService.DeclineAsync(_currentSession, _pendingProposal);
            SetState("已拒绝该动作。");
        }
        catch (AssistantActionException exception)
        {
            SetState(exception.Message);
        }
        finally
        {
            _pendingProposal = null;
            HidePendingAction();
            RenderMessages();
        }
    }

    private async void OnOpenModuleClicked(object sender, RoutedEventArgs e)
    {
        var module = (ModuleCombo.SelectedItem as ModuleChoice)?.Module ?? AssistantModule.Assistant;
        await _executeHostResultAsync(new AssistantActionExecutionResult(
            true,
            "已打开详情页。",
            HostCommand: new AssistantHostCommand(AssistantHostCommandKind.NavigateModule, module)));
    }

    private void RenderMessages()
    {
        MessagesPanel.Children.Clear();
        if (_currentSession is null || _currentSession.Messages.Count == 0)
        {
            MessagesPanel.Children.Add(new TextBlock
            {
                Text = "你好，我可以协助处理提醒、证据、报告、采集和日报任务。",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 112, 133)),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(18, 70, 18, 0)
            });
            return;
        }

        foreach (var message in _currentSession.Messages)
        {
            MessagesPanel.Children.Add(CreateMessageElement(message));
        }
        Dispatcher.BeginInvoke(() => MessagesScrollViewer.ScrollToEnd());
    }

    private static FrameworkElement CreateMessageElement(AssistantMessage message)
    {
        var stack = new StackPanel();
        if (message.Role == AssistantMessageRole.Assistant)
        {
            stack.Children.Add(new FlowDocumentScrollViewer
            {
                Document = AssistantMarkdownRenderer.Render(message.Text),
                IsToolBarVisible = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = System.Windows.Media.Brushes.Transparent,
                IsTabStop = false
            });
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text = message.Text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13.5,
                Foreground = message.Role == AssistantMessageRole.User
                    ? System.Windows.Media.Brushes.White
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 32, 51))
            });
        }

        if (message.Attachments.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", message.Attachments.Select(item => item.Name)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Opacity = 0.75,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
        if (message.Action is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"动作：{message.Action.Summary} · {DisplayStatus(message.Action.Status)}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 112, 133)),
                Margin = new Thickness(0, 7, 0, 0)
            });
        }

        return new Border
        {
            Child = stack,
            MaxWidth = 650,
            HorizontalAlignment = message.Role == AssistantMessageRole.User
                ? System.Windows.HorizontalAlignment.Right
                : System.Windows.HorizontalAlignment.Left,
            Background = message.Role == AssistantMessageRole.User
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 134, 108))
                : System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(215, 226, 232)),
            BorderThickness = message.Role == AssistantMessageRole.User ? new Thickness(0) : new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 10)
        };
    }

    private void RefreshAttachments()
    {
        AttachmentItems.ItemsSource = null;
        AttachmentItems.ItemsSource = _attachments.ToList();
        AttachmentPanel.Visibility = _attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowPendingAction(AssistantActionProposal proposal)
    {
        PendingActionSummary.Text = proposal.Summary;
        PendingActionCard.Visibility = Visibility.Visible;
    }

    private void HidePendingAction()
    {
        PendingActionCard.Visibility = Visibility.Collapsed;
        PendingActionSummary.Text = "";
    }

    private void SetBusy(bool busy)
    {
        SendButton.IsEnabled = !busy;
        AddAttachmentButton.IsEnabled = !busy;
        NewSessionButton.IsEnabled = !busy;
        DeleteSessionButton.IsEnabled = !busy;
        ModuleCombo.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SetState(busy ? "正在生成回复..." : RequestStateText.Text);
    }

    private void SetState(string state)
    {
        RequestStateText.Text = state;
    }

    private static string DisplayStatus(AssistantActionStatus status)
    {
        return status switch
        {
            AssistantActionStatus.Executed => "已执行",
            AssistantActionStatus.Declined => "已拒绝",
            AssistantActionStatus.Failed => "失败",
            AssistantActionStatus.Expired => "已过期",
            AssistantActionStatus.Superseded => "已失效",
            _ => "等待确认"
        };
    }

    private static IReadOnlyList<ModuleChoice> CreateModuleChoices()
    {
        return
        [
            new(AssistantModule.Assistant, "全部功能"),
            new(AssistantModule.Reminders, "提醒"),
            new(AssistantModule.Model, "模型"),
            new(AssistantModule.Evidence, "证据"),
            new(AssistantModule.Review, "回顾"),
            new(AssistantModule.Reports, "报告"),
            new(AssistantModule.Capture, "采集"),
            new(AssistantModule.Tasks, "任务")
        ];
    }

    private sealed record ModuleChoice(AssistantModule Module, string Name);
}
