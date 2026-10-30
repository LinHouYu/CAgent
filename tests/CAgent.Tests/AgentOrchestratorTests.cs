using CAgent.Core.Abstractions;
using CAgent.Core.Agent;
using CAgent.Core.Configuration;
using CAgent.Tools;

namespace CAgent.Tests;

public sealed class AgentOrchestratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceDir;

    public AgentOrchestratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cagent_orch_test_{Guid.NewGuid():N}");
        _workspaceDir = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspaceDir);
    }

    [Fact]
    public async Task ManualApprovalFlow_PausesUntilApproved_ThenExecutesToolAndCompletes()
    {
        using var configSvc = CreateConfig(autoApprove: false, maxSteps: 5);
        var account = configSvc.Current.GetActiveAccount();

        var bridge = new ScriptedMockBridge(
        [
            """
            我将在工作目录创建 `hello.txt`：
            ```cagent
            {"tool":"write_file","args":{"path":"hello.txt","content":"Hello from Copilot!"}}
            ```
            """,
            "文件 `hello.txt` 已创建完毕！"
        ]);

        var orchestrator = new AgentOrchestrator(
            configSvc,
            bridge,
            BuiltInTools.CreateDefaultRegistry(),
            randomMsProvider: (_, _) => 5);

        var waitingSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.StateChanged += snap =>
        {
            if (snap.State == AgentRunState.WaitingApproval)
            {
                waitingSignal.TrySetResult();
            }
        };

        var runTask = orchestrator.SendUserTaskAsync(account, "帮我创建 hello.txt");

        await waitingSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // Before approval, file MUST NOT exist yet!
        Assert.False(File.Exists(Path.Combine(_workspaceDir, "hello.txt")));

        Assert.True(orchestrator.ApprovePendingTool(account.Id));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(File.Exists(Path.Combine(_workspaceDir, "hello.txt")));
        Assert.Equal("Hello from Copilot!", await File.ReadAllTextAsync(Path.Combine(_workspaceDir, "hello.txt")));
        Assert.Equal(2, bridge.SentMessages.Count);
        Assert.Contains("[CAgent Tool Result | tool=write_file | status=OK]", bridge.SentMessages[1]);
    }

    [Fact]
    public async Task RejectPendingTool_BlocksExecutionAndReturnsToIdle()
    {
        using var configSvc = CreateConfig(autoApprove: false, maxSteps: 5);
        var account = configSvc.Current.GetActiveAccount();

        var bridge = new ScriptedMockBridge(
        [
            """
            ```cagent
            {"tool":"write_file","args":{"path":"blocked.txt","content":"should never exist"}}
            ```
            """
        ]);

        var orchestrator = new AgentOrchestrator(
            configSvc,
            bridge,
            BuiltInTools.CreateDefaultRegistry(),
            randomMsProvider: (_, _) => 0);

        var waitingSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.StateChanged += snap =>
        {
            if (snap.State == AgentRunState.WaitingApproval)
            {
                waitingSignal.TrySetResult();
            }
        };

        var runTask = orchestrator.SendUserTaskAsync(account, "测试拒绝执行");
        await waitingSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(orchestrator.RejectPendingTool(account.Id));
        await runTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(File.Exists(Path.Combine(_workspaceDir, "blocked.txt")));
        Assert.Single(bridge.SentMessages); // Did not send follow-up message
    }

    [Fact]
    public async Task AutoApproveWithMaxStepsCircuitBreaker_PausesWhenLimitReached()
    {
        using var configSvc = CreateConfig(autoApprove: true, maxSteps: 2);
        var account = configSvc.Current.GetActiveAccount();

        var bridge = new ScriptedMockBridge(
        [
            "```cagent\n{\"tool\":\"list_dir\",\"args\":{\"path\":\".\"}}\n```",
            "```cagent\n{\"tool\":\"write_file\",\"args\":{\"path\":\"step2.txt\",\"content\":\"ok\"}}\n```",
            "```cagent\n{\"tool\":\"read_file\",\"args\":{\"path\":\"step2.txt\"}}\n```",
            "全部 3 步执行完成！"
        ]);

        var orchestrator = new AgentOrchestrator(
            configSvc,
            bridge,
            BuiltInTools.CreateDefaultRegistry(),
            randomMsProvider: (_, _) => 5);

        var breakerPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.StateChanged += snap =>
        {
            if (snap.State == AgentRunState.WaitingApproval)
            {
                breakerPaused.TrySetResult();
            }
        };

        var runTask = orchestrator.SendUserTaskAsync(account, "连续执行三步");

        // Steps 1 and 2 run automatically; Step 3 must trip the circuit breaker (MaxAutoSteps = 2)
        await breakerPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(Path.Combine(_workspaceDir, "step2.txt")));

        // User approves continuing past the circuit breaker
        Assert.True(orchestrator.ApprovePendingTool(account.Id));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, bridge.SentMessages.Count);
        var timeline = orchestrator.GetTimeline(account.Id);
        Assert.Contains(timeline, e => e.Kind == TimelineEntryKind.SystemNotice && e.Title.Contains("熔断"));
    }

    [Fact]
    public async Task EmergencyStop_ImmediatelyKillsRunningShellProcess()
    {
        using var configSvc = CreateConfig(autoApprove: true, maxSteps: 5);
        var account = configSvc.Current.GetActiveAccount();

        var longSleep = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";
        var bridge = new ScriptedMockBridge(
        [
            $$$"""
            ```cagent
            {"tool":"run_shell","args":{"command":"{{{longSleep}}}"}}
            ```
            """
        ]);

        var orchestrator = new AgentOrchestrator(
            configSvc,
            bridge,
            BuiltInTools.CreateDefaultRegistry(),
            randomMsProvider: (_, _) => 0);

        var executingSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.StateChanged += snap =>
        {
            if (snap.State == AgentRunState.ExecutingTool)
            {
                executingSignal.TrySetResult();
            }
        };

        var runTask = orchestrator.SendUserTaskAsync(account, "执行长耗时命令");
        await executingSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);

        orchestrator.EmergencyStop(account.Id);
        await runTask.WaitAsync(TimeSpan.FromSeconds(4));

        Assert.False(orchestrator.IsBusy);
        Assert.Contains(orchestrator.GetTimeline(account.Id), e => e.Title.Contains("急停"));
    }

    [Fact]
    public async Task StartNewChatAsync_WaitsForLoginIfNeeded_AttachesSystemPromptFile_AndAutoScansWorkspace()
    {
        using var configSvc = CreateConfig(autoApprove: true, maxSteps: 10);
        var account = configSvc.Current.GetActiveAccount();
        await File.WriteAllTextAsync(Path.Combine(_workspaceDir, "README_DEMO.md"), "# Demo");

        var bridge = new ScriptedMockBridge(
        [
            "```cagent\n{\"tool\":\"list_dir\",\"args\":{\"path\":\".\"}}\n```",
            "CAgent 已就绪，当前工作目录包含 `README_DEMO.md`！"
        ])
        {
            SimulateWaitingForLoginOnce = true
        };

        var orchestrator = new AgentOrchestrator(
            configSvc,
            bridge,
            BuiltInTools.CreateDefaultRegistry(),
            randomMsProvider: (_, _) => 0)
        {
            LoginPollIntervalMs = 25
        };

        await orchestrator.StartNewChatAsync(account);

        Assert.Equal(1, bridge.NewConversationCalls);
        Assert.Equal(2, bridge.SentMessages.Count);
        Assert.Contains("[CAgent自动消息]", bridge.SentMessages[0]);

        // Turn 1 must attach generated .cagent/cagent_system_prompt.txt
        var firstAttachments = bridge.SentAttachments[0];
        Assert.NotNull(firstAttachments);
        var attachedPromptPath = Assert.Single(firstAttachments!);
        Assert.EndsWith("cagent_system_prompt.txt", attachedPromptPath);
        Assert.True(File.Exists(attachedPromptPath));

        // Turn 2 automatically sent list_dir output containing README_DEMO.md
        Assert.Contains("README_DEMO.md", bridge.SentMessages[1]);
        Assert.Contains("[CAgent自动消息]", bridge.SentMessages[1]);
    }

    [Fact]
    public async Task ExecuteMode_ContinuousMultiTurnLoop_AppendsAutoFooterEveryTurn_AndKeepsUserTimelineClean()
    {
        using var configSvc = CreateConfig(autoApprove: true, maxSteps: 15);
        var account = configSvc.Current.GetActiveAccount();

        var bridge = new ScriptedMockBridge(
        [
            "```cagent\n{\"tool\":\"write_file\",\"args\":{\"path\":\"index.html\",\"content\":\"<h1>Part 1</h1>\\n\",\"append\":false}}\n```",
            "```cagent\n{\"tool\":\"write_file\",\"args\":{\"path\":\"index.html\",\"content\":\"<p>Part 2</p>\",\"append\":true}}\n```",
            "```cagent\n{\"tool\":\"screenshot\",\"args\":{}}\n```",
            "页面已通过截图验收，全部完成！"
        ]);

        var registry = BuiltInTools.CreateDefaultRegistry(customScreenCapture: new TinyPngCaptureBackend());
        var orchestrator = new AgentOrchestrator(configSvc, bridge, registry, (_, _) => 0);

        await orchestrator.SendUserTaskAsync(account, "做一个双段网页并主动截图检查");

        // All 4 turns ran continuously without pausing for approval!
        Assert.Equal(4, bridge.SentMessages.Count);
        Assert.All(bridge.SentMessages, msg => Assert.Contains("[CAgent自动消息]", msg));

        // Combined chunked file must exist in workspace
        var html = await File.ReadAllTextAsync(Path.Combine(_workspaceDir, "index.html"));
        Assert.Contains("<h1>Part 1</h1>", html);
        Assert.Contains("<p>Part 2</p>", html);

        // Turn 4 (after screenshot) forwarded the PNG attachment to Copilot
        Assert.NotNull(bridge.SentAttachments[3]);
        Assert.Single(bridge.SentAttachments[3]!);

        // User timeline bubble MUST stay clean (no [CAgent自动消息] noise shown to user)
        var userBubble = Assert.Single(orchestrator.GetTimeline(account.Id), e => e.Kind == TimelineEntryKind.UserMessage);
        Assert.Equal("做一个双段网页并主动截图检查", userBubble.Content);
    }

    [Fact]
    public async Task PlanMode_AllowsReadOnlyTools_AndBlocksMutatingToolsWithFeedbackToCopilot()
    {
        using var configSvc = CreateConfig(autoApprove: true, maxSteps: 10);
        configSvc.Update(c => c.InteractionMode = AgentInteractionMode.Plan);
        var account = configSvc.Current.GetActiveAccount();

        var bridge = new ScriptedMockBridge(
        [
            // Read-only tool -> allowed in Plan mode
            "```cagent\n{\"tool\":\"list_dir\",\"args\":{\"path\":\".\"}}\n```",
            // Mutating tool -> MUST be blocked in Plan mode!
            "```cagent\n{\"tool\":\"write_file\",\"args\":{\"path\":\"unauthorized.txt\",\"content\":\"nope\"}}\n```",
            "收到规划模式限制，这是我的完整实现方案，请确认后切换到执行模式。"
        ]);

        var orchestrator = new AgentOrchestrator(
            configSvc,
            bridge,
            BuiltInTools.CreateDefaultRegistry(),
            randomMsProvider: (_, _) => 0);

        await orchestrator.SendUserTaskAsync(account, "帮我规划一下重构方案");

        Assert.False(File.Exists(Path.Combine(_workspaceDir, "unauthorized.txt")));
        Assert.Equal(3, bridge.SentMessages.Count);
        Assert.Contains("status=OK", bridge.SentMessages[1]);
        Assert.Contains("status=BLOCKED_BY_PLAN_MODE", bridge.SentMessages[2]);

        var timeline = orchestrator.GetTimeline(account.Id);
        Assert.Contains(timeline, e => e.ApprovalStatus == ToolApprovalStatus.BlockedByPlanMode);
    }

    [Fact]
    public async Task ResolveEffectiveWorkspace_StripsExplorerQuotesAndAutoCreatesDirectory()
    {
        var newSubWs = Path.Combine(_tempDir, "auto_created_project");
        using var configSvc = CreateConfig(autoApprove: true, maxSteps: 5);
        configSvc.Update(c => c.LastWorkspacePath = $"  \"{newSubWs}\"  ");
        var account = configSvc.Current.GetActiveAccount();

        var bridge = new ScriptedMockBridge(
        [
            "```cagent\n{\"tool\":\"write_file\",\"args\":{\"path\":\"/hello_rootless.txt\",\"content\":\"strictly_in_ws\"}}\n```",
            "完成"
        ]);

        var orchestrator = new AgentOrchestrator(configSvc, bridge, BuiltInTools.CreateDefaultRegistry(), (_, _) => 0);
        await orchestrator.SendUserTaskAsync(account, "测试路径防漂移");

        Assert.True(Directory.Exists(newSubWs));
        Assert.Equal("strictly_in_ws", await File.ReadAllTextAsync(Path.Combine(newSubWs, "hello_rootless.txt")));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    private ConfigService CreateConfig(bool autoApprove, int maxSteps)
    {
        var svc = new ConfigService(Path.Combine(_tempDir, $"cfg_{Guid.NewGuid():N}.json"));
        svc.Update(cfg =>
        {
            cfg.LastWorkspacePath = _workspaceDir;
            cfg.AutoApproveTools = autoApprove;
            cfg.MaxAutoSteps = maxSteps;
            cfg.DelayMinMs = 0;
            cfg.DelayMaxMs = 10;
        });
        return svc;
    }

    private sealed class TinyPngCaptureBackend : IScreenCaptureBackend
    {
        public Task<(int Width, int Height)> CapturePngAsync(string outputPath, CancellationToken cancellationToken)
        {
            var pixels = new byte[2 * 2 * 4];
            using var fs = File.Create(outputPath);
            PngEncoder.WriteBgra32AsPng(fs, 2, 2, pixels);
            return Task.FromResult((2, 2));
        }
    }

    private sealed class ScriptedMockBridge(IReadOnlyList<string> replies) : IBrowserBridge
    {
        private int _index;
        public List<string> SentMessages { get; } = [];
        public List<IReadOnlyList<string>?> SentAttachments { get; } = [];
        public int NewConversationCalls { get; private set; }
        public bool SimulateWaitingForLoginOnce { get; set; }

        public event Action<string, BridgeStatusSnapshot>? StatusChanged;

        public Task<BridgeStatusSnapshot> EnsureConnectedAsync(AccountProfile account, CancellationToken cancellationToken = default)
        {
            var snap = new BridgeStatusSnapshot(BridgeConnectionState.Ready, "Ready");
            StatusChanged?.Invoke(account.Id, snap);
            return Task.FromResult(snap);
        }

        public Task<BridgeStatusSnapshot> StartNewConversationAsync(AccountProfile account, CancellationToken cancellationToken = default)
        {
            NewConversationCalls++;
            if (SimulateWaitingForLoginOnce)
            {
                SimulateWaitingForLoginOnce = false;
                var loginSnap = new BridgeStatusSnapshot(BridgeConnectionState.WaitingForLogin, "Waiting for login");
                StatusChanged?.Invoke(account.Id, loginSnap);
                return Task.FromResult(loginSnap);
            }
            return EnsureConnectedAsync(account, cancellationToken);
        }

        public Task<BridgeStatusSnapshot> CheckStatusAsync(AccountProfile account, CancellationToken cancellationToken = default) =>
            EnsureConnectedAsync(account, cancellationToken);

        public Task<string> SendAndAwaitReplyAsync(
            AccountProfile account,
            string message,
            IReadOnlyList<string>? attachmentPaths = null,
            Action<string>? onStreamingDelta = null,
            CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            SentAttachments.Add(attachmentPaths);
            var reply = _index < replies.Count ? replies[_index++] : "完成";
            onStreamingDelta?.Invoke(reply);
            return Task.FromResult(reply);
        }

        public Task<CalibratedSelector> CalibrateInputSelectorAsync(AccountProfile account, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CalibratedSelector { CssSelector = "#userInput" });

        public Task DisconnectAccountAsync(string accountId) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
