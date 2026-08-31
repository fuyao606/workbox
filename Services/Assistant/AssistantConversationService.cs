using Timenotifier.Models;
using Timenotifier.Services.Infrastructure;

namespace Timenotifier.Services.Assistant;

public sealed record AssistantTurnRequest(
    AssistantSession Session,
    string Message,
    AssistantModule Module,
    IReadOnlyList<PreparedAssistantAttachment> Attachments);

public sealed record AssistantTurnResult(
    AssistantMessage AssistantMessage,
    AssistantActionProposal? Proposal,
    AssistantActionExecutionResult? ImmediateActionResult);

public interface IAssistantConversationService
{
    AssistantSession CreateTransientSession();
    Task<AssistantSessionLoadResult> LoadSessionsAsync(CancellationToken cancellationToken = default);
    Task<AssistantTurnResult> SendAsync(AssistantTurnRequest request, CancellationToken cancellationToken = default);
    Task<AssistantActionExecutionResult> ExecuteAsync(
        AssistantSession session,
        AssistantActionProposal proposal,
        CancellationToken cancellationToken = default);
    Task DeclineAsync(
        AssistantSession session,
        AssistantActionProposal proposal,
        CancellationToken cancellationToken = default);
}

public sealed class AssistantConversationService(
    IAssistantSessionStore sessionStore,
    IAssistantContextProvider contextProvider,
    AssistantPromptBuilder promptBuilder,
    IAssistantModelGateway modelGateway,
    IAssistantActionHandler actionHandler,
    ITimeProvider timeProvider) : IAssistantConversationService
{
    private int _isSending;
    private AssistantActionProposal? _pendingProposal;
    private string? _pendingSessionId;

    public AssistantSession CreateTransientSession()
    {
        return new AssistantSession
        {
            CreatedAt = timeProvider.Now,
            UpdatedAt = timeProvider.Now
        };
    }

    public Task<AssistantSessionLoadResult> LoadSessionsAsync(CancellationToken cancellationToken = default)
    {
        return sessionStore.LoadAllAsync(cancellationToken);
    }

    public async Task<AssistantTurnResult> SendAsync(
        AssistantTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _isSending, 1, 0) != 0)
        {
            throw new InvalidOperationException("AI 助手正在处理上一条请求，请等待或取消。");
        }

        try
        {
            var messageText = request.Message.Trim();
            if (messageText.Length == 0)
            {
                throw new InvalidOperationException("消息不能为空。");
            }

            SupersedePending(request.Session);
            var userMessage = new AssistantMessage
            {
                Role = AssistantMessageRole.User,
                Text = messageText,
                CreatedAt = timeProvider.Now,
                Attachments = request.Attachments.Select(attachment => new AssistantAttachmentMetadata
                {
                    Name = attachment.Metadata.Name,
                    MediaType = attachment.Metadata.MediaType,
                    SizeBytes = attachment.Metadata.SizeBytes
                }).ToList()
            };
            request.Session.Messages.Add(userMessage);
            if (request.Session.Messages.Count == 1)
            {
                request.Session.Title = BuildTitle(messageText);
            }
            request.Session.UpdatedAt = timeProvider.Now;
            await sessionStore.SaveAsync(request.Session, cancellationToken);

            var context = await contextProvider.BuildAsync(request.Module, messageText, cancellationToken);
            var prompt = promptBuilder.Build(
                request.Session,
                messageText,
                request.Module,
                context,
                request.Attachments,
                actionHandler.Actions);
            var images = request.Attachments
                .Select(attachment => attachment.ImageInput)
                .OfType<ModelImageInput>()
                .ToList();
            var raw = await modelGateway.CompleteAsync(prompt, images, cancellationToken);
            var parsed = AssistantResponseParser.Parse(raw);
            var reply = string.IsNullOrWhiteSpace(parsed.Reply) ? "模型没有返回可显示内容。" : parsed.Reply;
            AssistantActionProposal? proposal = null;
            AssistantActionExecutionResult? immediateResult = null;
            AssistantActionRecord? actionRecord = null;
            if (parsed.Action is not null)
            {
                try
                {
                    var created = actionHandler.CreateProposal(parsed.Action);
                    actionRecord = new AssistantActionRecord
                    {
                        ProposalId = created.Id,
                        Name = created.Name,
                        Summary = created.Summary,
                        Risk = created.Risk,
                        Status = AssistantActionStatus.Proposed
                    };
                    if (created.Risk == AssistantActionRisk.Immediate)
                    {
                        immediateResult = await actionHandler.ExecuteAsync(created, cancellationToken);
                        actionRecord.Status = immediateResult.Succeeded
                            ? AssistantActionStatus.Executed
                            : AssistantActionStatus.Failed;
                        reply = $"{reply}{Environment.NewLine}{Environment.NewLine}{immediateResult.Message}";
                    }
                    else
                    {
                        proposal = created;
                        _pendingProposal = created;
                        _pendingSessionId = request.Session.Id;
                    }
                }
                catch (AssistantActionException exception)
                {
                    reply = $"{reply}{Environment.NewLine}{Environment.NewLine}【动作未执行】{exception.Message}";
                }
            }

            var assistantMessage = new AssistantMessage
            {
                Role = AssistantMessageRole.Assistant,
                Text = reply,
                CreatedAt = timeProvider.Now,
                Action = actionRecord
            };
            request.Session.Messages.Add(assistantMessage);
            request.Session.UpdatedAt = timeProvider.Now;
            await sessionStore.SaveAsync(request.Session, cancellationToken);
            return new AssistantTurnResult(assistantMessage, proposal, immediateResult);
        }
        finally
        {
            Interlocked.Exchange(ref _isSending, 0);
        }
    }

    public async Task<AssistantActionExecutionResult> ExecuteAsync(
        AssistantSession session,
        AssistantActionProposal proposal,
        CancellationToken cancellationToken = default)
    {
        EnsureCurrentProposal(session, proposal);
        var result = await actionHandler.ExecuteAsync(proposal, cancellationToken);
        UpdateActionStatus(session, proposal.Id, result.Succeeded
            ? AssistantActionStatus.Executed
            : AssistantActionStatus.Failed);
        _pendingProposal = null;
        _pendingSessionId = null;
        await sessionStore.SaveAsync(session, cancellationToken);
        return result;
    }

    public async Task DeclineAsync(
        AssistantSession session,
        AssistantActionProposal proposal,
        CancellationToken cancellationToken = default)
    {
        EnsureCurrentProposal(session, proposal);
        proposal.IsExpired = true;
        UpdateActionStatus(session, proposal.Id, AssistantActionStatus.Declined);
        _pendingProposal = null;
        _pendingSessionId = null;
        await sessionStore.SaveAsync(session, cancellationToken);
    }

    private void SupersedePending(AssistantSession session)
    {
        if (_pendingProposal is null)
        {
            return;
        }
        _pendingProposal.IsExpired = true;
        if (string.Equals(_pendingSessionId, session.Id, StringComparison.OrdinalIgnoreCase))
        {
            UpdateActionStatus(session, _pendingProposal.Id, AssistantActionStatus.Superseded);
        }
        _pendingProposal = null;
        _pendingSessionId = null;
    }

    private void EnsureCurrentProposal(AssistantSession session, AssistantActionProposal proposal)
    {
        if (_pendingProposal is null
            || !string.Equals(_pendingProposal.Id, proposal.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_pendingSessionId, session.Id, StringComparison.OrdinalIgnoreCase)
            || proposal.IsExpired)
        {
            throw new AssistantActionException("动作提案已过期，请重新发起。");
        }
    }

    private static void UpdateActionStatus(
        AssistantSession session,
        string proposalId,
        AssistantActionStatus status)
    {
        var record = session.Messages
            .Select(message => message.Action)
            .LastOrDefault(action => action?.ProposalId == proposalId);
        if (record is not null)
        {
            record.Status = status;
        }
    }

    private static string BuildTitle(string message)
    {
        var firstLine = message
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "新对话";
        return firstLine.Length <= 36 ? firstLine : firstLine[..36] + "...";
    }
}
