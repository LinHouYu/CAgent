using System.Collections.Concurrent;
using System.Text;
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
    private readonly ConcurrentDictionary<string, bool> _bootstrappedAccounts = new();
    private readonly object _runLock = new();
    private CancellationTokenSource? _activeRunCts;

    public int LoginWaitTimeoutSeconds { get; set; } = 120;
    public int LoginPollIntervalMs { get; set; } = 1200;

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

    public bool IsAccountBootstrapped(string accountId) =>
        _bootstrappedAccounts.TryGetValue(accountId, out var ok) && ok;

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

    /// <summary>
    /// One-click EAgent-style "Start New Chat": connects/launches browser, resets Copilot to a fresh chat,
    /// waits for login if necessary, attaches cagent_system_prompt.txt, sends bootstrap protocol, and scans workspace.
    /// </summary>
    public async Task StartNewChatAsync(AccountProfile account, CancellationToken cancellationToken = default)
    {
        using var runScope = BeginExclusiveRun(cancellationToken, out var ct);

        try
        {
            var workspace = ResolveEffectiveWorkspace();
            ClearTimeline(account.Id);
            _bootstrappedAccounts[account.Id] = false;

            AddEntry(new TimelineEntry
            {
                AccountId = account.Id,
                Kind = TimelineEntryKind.SystemNotice,
                Title = "✨ 开启新对话",
                Content = $"正在连接 {account.DisplayName} 并准备自动注入系统提示词（工作目录：{workspace}）..."
            });

            EmitState(account.Id, AgentRunState.SendingOrStreaming, "正在启动/连接浏览器并开启新对话...", 0);

            var status = await _bridge.StartNewConversationAsync(account, ct).ConfigureAwait(false);
            if (status.State == BridgeConnectionState.Error)
            {
                AddEntry(new TimelineEntry
                {
                    AccountId = account.Id,
                    Kind = TimelineEntryKind.Error,
                    Title = "连接浏览器失败",
                    Content = status.Message
                });
                EmitState(account.Id, AgentRunState.Error, status.Message, 0);
                return;
            }

            if (status.State != BridgeConnectionState.Ready)
            {
                AddEntry(new TimelineEntry
                {
                    AccountId = account.Id,
                    Kind = TimelineEntryKind.SystemNotice,
                    Title = "⏳ 等待浏览器登录就绪",
                    Content = "请在打开的浏览器窗口中登录 Copilot 账号，检测到聊天输入框后将自动注入提示词并扫描工作目录..."
                });

                var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, LoginWaitTimeoutSeconds));
                while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    EmitState(account.Id, AgentRunState.SendingOrStreaming, "等待浏览器登录完成以自动初始化新对话...", 0);
                    await Task.Delay(Math.Max(20, LoginPollIntervalMs), ct).ConfigureAwait(false);
                    status = await _bridge.CheckStatusAsync(account, ct).ConfigureAwait(false);
                    if (status.State == BridgeConnectionState.Ready)
                    {
                        break;
                    }
                }

                if (status.State != BridgeConnectionState.Ready)
                {
                    EmitState(account.Id, AgentRunState.Error, "等待浏览器聊天框就绪超时，请登录完成后再次点击「新建对话」", 0);
                    return;
                }
            }

            var promptFile = WriteSystemPromptFile(workspace);
            var mode = _configService.Current.InteractionMode;
            var bootstrapText = ToolCallParser.AppendAutoReinforcementFooter(
                ToolCallParser.BuildBootstrapSystemPrompt(workspace, mode),
                workspace,
                mode);

            _bootstrappedAccounts[account.Id] = true;

            await RunConversationLoopCoreAsync(
                account,
                bootstrapText,
                initialAttachments: [promptFile],
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EmitState(account.Id, AgentRunState.Stopped, "操作已中止", 0);
        }
        catch (Exception ex)
        {
            AddEntry(new TimelineEntry
            {
                AccountId = account.Id,
                Kind = TimelineEntryKind.Error,
                Title = "开启新对话异常",
                Content = ex.Message
            });
            EmitState(account.Id, AgentRunState.Error, $"异常: {ex.Message}", 0);
        }
    }

    public async Task BootstrapWorkspaceAsync(AccountProfile account, CancellationToken cancellationToken = default)
    {
        using var runScope = BeginExclusiveRun(cancellationToken, out var ct);

        try
        {
            var workspace = ResolveEffectiveWorkspace();
            var promptFile = WriteSystemPromptFile(workspace);
            var mode = _configService.Current.InteractionMode;
            var prompt = ToolCallParser.AppendAutoReinforcementFooter(
                ToolCallParser.BuildBootstrapSystemPrompt(workspace, mode),
                workspace,
                mode);

            AddEntry(new TimelineEntry
            {
                AccountId = account.Id,
                Kind = TimelineEntryKind.SystemNotice,
                Title = "初始化 Agent 协议",
                Content = $"已向 {account.DisplayName} 自动发送 System Prompt 并扫描工作目录：{workspace}"
            });

            _bootstrappedAccounts[account.Id] = true;
            await RunConversationLoopCoreAsync(account, prompt, [promptFile], ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EmitState(account.Id, AgentRunState.Stopped, "操作已中止", 0);
        }
        catch (Exception ex)
        {
            AddEntry(new TimelineEntry
            {
                AccountId = account.Id,
                Kind = TimelineEntryKind.Error,
                Title = "初始化异常",
                Content = ex.Message
            });
            EmitState(account.Id, AgentRunState.Error, $"异常: {ex.Message}", 0);
        }
    }

    public async Task SendUserTaskAsync(
        AccountProfile account,
        string userMessage,
        IReadOnlyList<string>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage) && (attachments is null || attachments.Count == 0))
        {
            return;
        }

        using var runScope = BeginExclusiveRun(cancellationToken, out var ct);

        try
        {
            var workspace = ResolveEffectiveWorkspace();
            var mode = _configService.Current.InteractionMode;

            // Keep user's UI timeline bubble clean (without internal [CAgent自动消息] boilerplate)
            AddEntry(new TimelineEntry
            {
                AccountId = account.Id,
                Kind = TimelineEntryKind.UserMessage,
                Title = mode == AgentInteractionMode.Plan ? "你 (规划模式)" : "你 (执行模式)",
                Content = userMessage,
                AttachmentPaths = attachments ?? []
            });

            string outboundMessage;
            List<string>? outboundAttachments = attachments is { Count: > 0 } ? [.. attachments] : null;

            if (!IsAccountBootstrapped(account.Id))
            {
                var promptFile = WriteSystemPromptFile(workspace);
                outboundAttachments ??= [];
                outboundAttachments.Insert(0, promptFile);
                outboundMessage = ToolCallParser.WrapFirstTurnUserTaskWithProtocol(userMessage, workspace, mode);
                _bootstrappedAccounts[account.Id] = true;
            }
            else
            {
                outboundMessage = ToolCallParser.AppendAutoReinforcementFooter(userMessage, workspace, mode);
            }

            await RunConversationLoopCoreAsync(account, outboundMessage, outboundAttachments, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EmitState(account.Id, AgentRunState.Stopped, "操作已中止", 0);
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
            EmitState(account.Id, AgentRunState.Error, $"异常: {ex.Message}", 0);
        }
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

    private async Task RunConversationLoopCoreAsync(
        AccountProfile account,
        string initialOutboundText,
        IReadOnlyList<string>? initialAttachments,
        CancellationToken ct)
    {
        var nextOutboundText = initialOutboundText;
        var nextOutboundFiles = initialAttachments;
        var autoStepCounter = 0;

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
            var workspace = ResolveEffectiveWorkspace();

            _toolRegistry.TryGetTool(call.ToolName, out var registeredTool);
            var isMutatingTool = registeredTool?.IsMutating ?? true;

            // Plan Mode Hard Guardrail: prevent mutating files/commands while user is discussing/planning
            if (cfg.InteractionMode == AgentInteractionMode.Plan && isMutatingTool)
            {
                var blockedEntry = new TimelineEntry
                {
                    AccountId = account.Id,
                    Kind = TimelineEntryKind.ToolCall,
                    Title = call.GetHumanSummary(),
                    Content = $"{call.RawJson}\n\n[规划模式保护] 当前处于【📋 规划模式】，已自动拦截修改操作。如需让 AI 直接动手实操，请切换为【⚡ 执行模式】。",
                    ToolRequest = call,
                    ApprovalStatus = ToolApprovalStatus.BlockedByPlanMode
                };
                AddEntry(blockedEntry);

                await ApplyAntiRiskDelayAsync(account.Id, autoStepCounter, ct).ConfigureAwait(false);

                var blockedEnvelope = ToolCallParser.FormatToolResultEnvelope(
                    call.ToolName,
                    isSuccess: false,
                    content: "当前处于【规划模式（Plan Mode）】，系统已禁止修改文件、执行终端命令或操控键鼠。请仅使用只读工具（list_dir / read_file / screenshot）分析现状，并向用户输出清晰的方案设计，等待用户切换到【执行模式】后再动手实操。",
                    customStatusLabel: "BLOCKED_BY_PLAN_MODE");

                nextOutboundText = ToolCallParser.AppendAutoReinforcementFooter(blockedEnvelope, workspace, cfg.InteractionMode);
                nextOutboundFiles = null;
                continue;
            }

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
            EmitState(account.Id, AgentRunState.ExecutingTool, $"正在自动执行 {call.ToolName} (第 {autoStepCounter} 步 / 累计 {totalCount} 次)...", autoStepCounter);

            workspace = ResolveEffectiveWorkspace();
            var execCtx = new ToolExecutionContext(
                workspace,
                TimeSpan.FromSeconds(_configService.Current.CommandTimeoutSec),
                ct);

            var result = await _toolRegistry.ExecuteAsync(call, execCtx).ConfigureAwait(false);
            toolEntry.ApprovalStatus = result.IsSuccess
                ? ToolApprovalStatus.ExecutedSuccess
                : ToolApprovalStatus.ExecutedFailed;
            toolEntry.AttachmentPaths = result.AttachmentPaths ?? [];

            var maxChars = Math.Max(400, _configService.Current.MaxPromptChars - 350);
            var truncated = OutputTruncator.TruncateIfNeeded(result.Output, maxChars, workspace);

            toolEntry.Content = $"{call.RawJson}\n\n--- 执行结果 ({(result.IsSuccess ? "成功" : "失败")}) ---\n{truncated.Content}";
            EntryUpdated?.Invoke(toolEntry);

            await ApplyAntiRiskDelayAsync(account.Id, autoStepCounter, ct).ConfigureAwait(false);

            var currentMode = _configService.Current.InteractionMode;
            var rawEnvelope = ToolCallParser.FormatToolResultEnvelope(
                call.ToolName,
                result.IsSuccess,
                truncated.Content,
                result.AttachmentPaths);

            nextOutboundText = ToolCallParser.AppendAutoReinforcementFooter(rawEnvelope, workspace, currentMode);
            nextOutboundFiles = result.AttachmentPaths;
        }
    }

    private async Task ApplyAntiRiskDelayAsync(string accountId, int autoStepCounter, CancellationToken ct)
    {
        var minDelay = _configService.Current.DelayMinMs;
        var maxDelay = _configService.Current.DelayMaxMs;
        var delayMs = _randomMsProvider(minDelay, maxDelay);
        if (delayMs > 0)
        {
            EmitState(
                accountId,
                AgentRunState.AntiRiskDelay,
                $"防风控节流等待 {delayMs / 1000.0:F1} 秒后自动继续...",
                autoStepCounter);
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
    }

    private static string WriteSystemPromptFile(string workspacePath)
    {
        var dir = Path.Combine(workspacePath, ".cagent");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "cagent_system_prompt.txt");
        var content = ToolCallParser.BuildSystemPromptAttachmentContent(workspacePath);
        File.WriteAllText(filePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return filePath;
    }

    public string ResolveEffectiveWorkspace()
    {
        var configured = _configService.Current.LastWorkspacePath?.Trim().Trim('"', '\'');
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var full = Path.GetFullPath(configured);
                if (!Directory.Exists(full))
                {
                    Directory.CreateDirectory(full);
                }
                return full;
            }
            catch
            {
                // Fall through if invalid path syntax
            }
        }
        return Directory.GetCurrentDirectory();
    }

    private ExclusiveRunScope BeginExclusiveRun(CancellationToken externalCt, out CancellationToken linkedToken)
    {
        CancellationTokenSource runCts;
        lock (_runLock)
        {
            _activeRunCts?.Cancel();
            runCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            _activeRunCts = runCts;
        }
        linkedToken = runCts.Token;
        return new ExclusiveRunScope(this, runCts);
    }

    private sealed class ExclusiveRunScope(AgentOrchestrator owner, CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            foreach (var kvp in owner._pendingApprovals)
            {
                kvp.Value.TrySetCanceled();
            }
            owner._pendingApprovals.Clear();

            lock (owner._runLock)
            {
                if (ReferenceEquals(owner._activeRunCts, cts))
                {
                    owner._activeRunCts = null;
                }
            }
            cts.Dispose();
        }
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
