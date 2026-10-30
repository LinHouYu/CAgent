using System.Collections.Concurrent;
using CAgent.Core.Abstractions;
using CAgent.Core.Configuration;
using CAgent.Core.Protocol;

namespace CAgent.Core.Agent;

public sealed class AgentOrchestrator
{
    private readonly ConfigService _configService;
    private readonly IBrowserBridge _bridge;
    private readonly ToolRegistry _toolRegistry;
    private readonly Func<int, int, int> _randomMsProvider;

    private readonly ConcurrentDictionary<string, List<TimelineEntry>> _timelines = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new();
    private readonly ConcurrentDictionary<string, int> _totalToolCalls = new();
    private readonly object _runLock = new();
    private CancellationTokenSource? _activeRunCts;

    public event Action<AgentStateSnapshot>? StateChanged;
    public event Action<TimelineEntry>? EntryAdded;
    public event Action<TimelineEntry>? EntryUpdated;

    public bool IsBusy
    {
        get
        {
            lock (_runLock)
            {
                return _activeRunCts is not null && !_activeRunCts.IsCancellationRequested;
            }
        }
    }

    public AgentOrchestrator(
        ConfigService configService,
        IBrowserBridge bridge,
        ToolRegistry toolRegistry,
        Func<int, int, int>? randomMsProvider = null)
    {
        _configService = configService;
        _bridge = bridge;
        _toolRegistry = toolRegistry;
        _randomMsProvider = randomMsProvider ?? ((min, max) => max <= min ? min : Random.Shared.Next(min, max + 1));
    }

    public IReadOnlyList<TimelineEntry> GetTimeline(string accountId)
    {
        var list = _timelines.GetOrAdd(accountId, _ => []);
        lock (list)
        {
            return list.ToArray();
        }
    }

    public void ClearTimeline(string accountId)
    {
        if (_timelines.TryGetValue(accountId, out var list))
        {
            lock (list)
            {
                list.Clear();
            }
        }
    }

    public Task BootstrapWorkspaceAsync(AccountProfile account, CancellationToken cancellationToken = default)
    {
        var workspace = ResolveEffectiveWorkspace();
        var prompt = ToolCallParser.BuildBootstrapSystemPrompt(workspace);
        AddEntry(new TimelineEntry
        {
            AccountId = account.Id,
            Kind = TimelineEntryKind.SystemNotice,
            Title = "初始化 Agent 协议",
            Content = $"已向 {account.DisplayName} 注入工作目录上下文：{workspace}"
        });

        return RunConversationLoopAsync(account, prompt, attachments: null, recordAsUserBubble: false, cancellationToken);
    }

    public Task SendUserTaskAsync(
        AccountProfile account,
        string userMessage,
        IReadOnlyList<string>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage) && (attachments is null || attachments.Count == 0))
        {
            return Task.CompletedTask;
        }

        return RunConversationLoopAsync(account, userMessage, attachments, recordAsUserBubble: true, cancellationToken);
    }

    public bool ApprovePendingTool(string accountId)
    {
        if (_pendingApprovals.TryRemove(accountId, out var tcs))
        {
            return tcs.TrySetResult(true);
        }
        return false;
    }

    public bool RejectPendingTool(string accountId)
    {
        if (_pendingApprovals.TryRemove(accountId, out var tcs))
        {
            return tcs.TrySetResult(false);
        }
        return false;
    }

    public void EmergencyStop(string? accountId = null)
    {
        CancellationTokenSource? cts;
        lock (_runLock)
        {
            cts = _activeRunCts;
            _activeRunCts = null;
        }

        foreach (var kvp in _pendingApprovals)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingApprovals.Clear();

        cts?.Cancel();

        var targetAccountId = accountId ?? _configService.Current.ActiveAccountId;
        AddEntry(new TimelineEntry
        {
            AccountId = targetAccountId,
            Kind = TimelineEntryKind.SystemNotice,
            Title = "⏹ 已触发一键急停",
            Content = "已中断当前本地命令执行与自动对话循环。"
        });
        EmitState(targetAccountId, AgentRunState.Stopped, "急停已生效", 0);
    }

    private async Task RunConversationLoopAsync(
        AccountProfile account,
        string initialMessage,
        IReadOnlyList<string>? attachments,
        bool recordAsUserBubble,
        CancellationToken externalCt)
    {
        CancellationTokenSource runCts;
        lock (_runLock)
        {
            _activeRunCts?.Cancel();
            runCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            _activeRunCts = runCts;
        }

        var ct = runCts.Token;

        if (recordAsUserBubble)
        {
            AddEntry(new TimelineEntry
            {
                AccountId = account.Id,
                Kind = TimelineEntryKind.UserMessage,
                Title = "你",
                Content = initialMessage,
                AttachmentPaths = attachments ?? []
            });
        }

        var nextOutboundText = initialMessage;
        var nextOutboundFiles = attachments;
        var autoStepCounter = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                EmitState(account.Id, AgentRunState.SendingOrStreaming, "等待 Copilot 思考与回复...", autoStepCounter);

                var assistantEntry = new TimelineEntry
                {
                    AccountId = account.Id,
                    Kind = TimelineEntryKind.AssistantMessage,
                    Title = account.DisplayName,
                    Content = "正在思考..."
                };
                AddEntry(assistantEntry);

                var fullReply = await _bridge.SendAndAwaitReplyAsync(
                    account,
                    nextOutboundText,
                    nextOutboundFiles,
                    onStreamingDelta: partial =>
                    {
                        assistantEntry.Content = partial;
                        EntryUpdated?.Invoke(assistantEntry);
                    },
                    cancellationToken: ct).ConfigureAwait(false);

                assistantEntry.Content = fullReply;
                EntryUpdated?.Invoke(assistantEntry);

                var toolCalls = ToolCallParser.ParseToolCalls(fullReply);
                if (toolCalls.Count == 0)
                {
                    EmitState(account.Id, AgentRunState.Idle, "回复完成，等待下一步指令", autoStepCounter);
                    return;
                }

                var call = toolCalls[0];
                var cfg = _configService.Current;
                var circuitBreakerTripped = cfg.AutoApproveTools && autoStepCounter >= cfg.MaxAutoSteps;

                var toolEntry = new TimelineEntry
                {
                    AccountId = account.Id,
                    Kind = TimelineEntryKind.ToolCall,
                    Title = call.GetHumanSummary(),
                    Content = call.RawJson,
                    ToolRequest = call,
                    ApprovalStatus = (!cfg.AutoApproveTools || circuitBreakerTripped)
                        ? ToolApprovalStatus.PendingApproval
                        : ToolApprovalStatus.ApprovedExecuting
                };
                AddEntry(toolEntry);

                if (!cfg.AutoApproveTools || circuitBreakerTripped)
                {
                    var reason = circuitBreakerTripped
                        ? $"已达到单轮自动执行步数上限 ({cfg.MaxAutoSteps} 步)，请人工确认是否继续执行：{call.ToolName}"
                        : $"等待您审批工具调用：{call.ToolName}";

                    if (circuitBreakerTripped)
                    {
                        AddEntry(new TimelineEntry
                        {
                            AccountId = account.Id,
                            Kind = TimelineEntryKind.SystemNotice,
                            Title = "⚠️ 自动步数熔断保护",
                            Content = reason
                        });
                    }

                    EmitState(account.Id, AgentRunState.WaitingApproval, reason, autoStepCounter);

                    var approvalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingApprovals[account.Id] = approvalTcs;
                    await using var reg = ct.Register(() => approvalTcs.TrySetCanceled(ct));

                    var approved = await approvalTcs.Task.ConfigureAwait(false);
                    if (!approved)
                    {
                        toolEntry.ApprovalStatus = ToolApprovalStatus.Rejected;
                        toolEntry.Content = $"{call.RawJson}\n\n[用户已拒绝执行]";
                        EntryUpdated?.Invoke(toolEntry);
                        EmitState(account.Id, AgentRunState.Idle, "已拒绝工具调用并暂停", autoStepCounter);
                        return;
                    }

                    // Reset circuit breaker counter once user explicitly approves continuation
                    autoStepCounter = 0;
                    toolEntry.ApprovalStatus = ToolApprovalStatus.ApprovedExecuting;
                    EntryUpdated?.Invoke(toolEntry);
                }

                autoStepCounter++;
                var totalCount = _totalToolCalls.AddOrUpdate(account.Id, 1, (_, prev) => prev + 1);
                EmitState(account.Id, AgentRunState.ExecutingTool, $"正在执行 {call.ToolName} (总第 {totalCount} 次)...", autoStepCounter);

                var workspace = ResolveEffectiveWorkspace();
                var execCtx = new ToolExecutionContext(
                    workspace,
                    TimeSpan.FromSeconds(_configService.Current.CommandTimeoutSec),
                    ct);

                var result = await _toolRegistry.ExecuteAsync(call, execCtx).ConfigureAwait(false);
                toolEntry.ApprovalStatus = result.IsSuccess
                    ? ToolApprovalStatus.ExecutedSuccess
                    : ToolApprovalStatus.ExecutedFailed;
                toolEntry.AttachmentPaths = result.AttachmentPaths ?? [];

                var maxChars = Math.Max(400, _configService.Current.MaxPromptChars - 200);
                var truncated = OutputTruncator.TruncateIfNeeded(result.Output, maxChars, workspace);

                toolEntry.Content = $"{call.RawJson}\n\n--- 执行结果 ({(result.IsSuccess ? "成功" : "失败")}) ---\n{truncated.Content}";
                EntryUpdated?.Invoke(toolEntry);

                // Anti-risk random throttling delay before auto-sending tool output back to Copilot
                var minDelay = _configService.Current.DelayMinMs;
                var maxDelay = _configService.Current.DelayMaxMs;
                var delayMs = _randomMsProvider(minDelay, maxDelay);
                if (delayMs > 0)
                {
                    EmitState(
                        account.Id,
                        AgentRunState.AntiRiskDelay,
                        $"防风控节流等待 {delayMs / 1000.0:F1} 秒后回传结果...",
                        autoStepCounter);
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }

                nextOutboundText = ToolCallParser.FormatToolResultEnvelope(
                    call.ToolName,
                    result.IsSuccess,
                    truncated.Content,
                    result.AttachmentPaths);
                nextOutboundFiles = result.AttachmentPaths;
            }
        }
        catch (OperationCanceledException)
        {
            EmitState(account.Id, AgentRunState.Stopped, "操作已中止", autoStepCounter);
        }
        catch (Exception ex)
        {
            AddEntry(new TimelineEntry
            {
                AccountId = account.Id,
                Kind = TimelineEntryKind.Error,
                Title = "执行异常",
                Content = ex.Message
            });
            EmitState(account.Id, AgentRunState.Error, $"异常: {ex.Message}", autoStepCounter);
        }
        finally
        {
            _pendingApprovals.TryRemove(account.Id, out _);
            lock (_runLock)
            {
                if (ReferenceEquals(_activeRunCts, runCts))
                {
                    _activeRunCts = null;
                }
            }
            runCts.Dispose();
        }
    }

    private string ResolveEffectiveWorkspace()
    {
        var configured = _configService.Current.LastWorkspacePath;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }
        return Directory.GetCurrentDirectory();
    }

    private void AddEntry(TimelineEntry entry)
    {
        var list = _timelines.GetOrAdd(entry.AccountId, _ => []);
        lock (list)
        {
            list.Add(entry);
        }
        EntryAdded?.Invoke(entry);
    }

    private void EmitState(string accountId, AgentRunState state, string statusText, int autoStep)
    {
        var total = _totalToolCalls.GetValueOrDefault(accountId, 0);
        StateChanged?.Invoke(new AgentStateSnapshot(accountId, state, statusText, autoStep, total));
    }
}
