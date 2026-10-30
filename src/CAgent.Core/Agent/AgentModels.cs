using CAgent.Core.Protocol;

namespace CAgent.Core.Agent;

public enum AgentRunState
{
    Idle,
    SendingOrStreaming,
    WaitingApproval,
    ExecutingTool,
    AntiRiskDelay,
    Stopped,
    Error
}

public enum TimelineEntryKind
{
    UserMessage,
    AssistantMessage,
    ToolCall,
    SystemNotice,
    Error
}

public enum ToolApprovalStatus
{
    None,
    PendingApproval,
    ApprovedExecuting,
    ExecutedSuccess,
    ExecutedFailed,
    Rejected,
    BlockedByPlanMode
}

public sealed class TimelineEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public required string AccountId { get; init; }
    public required TimelineEntryKind Kind { get; init; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public ToolCallRequest? ToolRequest { get; init; }
    public ToolApprovalStatus ApprovalStatus { get; set; } = ToolApprovalStatus.None;
    public IReadOnlyList<string> AttachmentPaths { get; set; } = [];
}

public sealed record AgentStateSnapshot(
    string AccountId,
    AgentRunState State,
    string StatusText,
    int CurrentAutoStep,
    int TotalToolCalls);
