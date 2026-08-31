using System.Text.Json;

namespace Timenotifier.Models;

public enum AssistantModule
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

public enum AssistantMessageRole
{
    User,
    Assistant,
    System
}

public enum AssistantActionRisk
{
    Immediate,
    Confirmation
}

public enum AssistantActionStatus
{
    Proposed,
    Executed,
    Declined,
    Failed,
    Expired,
    Superseded
}

[Flags]
public enum AssistantRefreshTargets
{
    None = 0,
    Reminders = 1,
    Model = 2,
    Evidence = 4,
    Review = 8,
    Reports = 16,
    Capture = 32,
    Automation = 64
}

public enum AssistantHostCommandKind
{
    NavigateModule,
    PreviewEvidenceImage,
    LoadReportDraft,
    SaveCurrentReport,
    ConfirmCurrentReport,
    NewDraftFromConfirmed,
    CopyCurrentReport,
    ExportWeeklyReport,
    OpenAutomationLogin,
    RunDailyAutomation
}

public sealed record AssistantHostCommand(
    AssistantHostCommandKind Kind,
    AssistantModule? Module = null,
    string? TargetId = null,
    string? Value = null);

public sealed class AssistantActionProposal
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public required JsonElement Arguments { get; init; }
    public required AssistantActionRisk Risk { get; init; }
    public required string Summary { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public bool IsExpired { get; set; }
}

public sealed record AssistantActionExecutionResult(
    bool Succeeded,
    string Message,
    AssistantRefreshTargets RefreshTargets = AssistantRefreshTargets.None,
    AssistantHostCommand? HostCommand = null);

public sealed class AssistantAttachmentMetadata
{
    public string Name { get; set; } = "";
    public string MediaType { get; set; } = "";
    public long SizeBytes { get; set; }
}

public sealed record ModelImageInput(
    byte[] JpegBytes,
    string MediaType,
    string DisplayName);

public sealed record AssistantProtectedValueSnapshot(IReadOnlyList<string> Values);

public sealed class AssistantActionRecord
{
    public string ProposalId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public AssistantActionRisk Risk { get; set; }
    public AssistantActionStatus Status { get; set; }
}

public sealed class AssistantMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AssistantMessageRole Role { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<AssistantAttachmentMetadata> Attachments { get; set; } = [];
    public AssistantActionRecord? Action { get; set; }
}

public sealed class AssistantSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新对话";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<AssistantMessage> Messages { get; set; } = [];
}
