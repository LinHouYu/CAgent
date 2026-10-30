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

    private sealed class ScriptedMockBridge(IReadOnlyList<string> replies) : IBrowserBridge
    {
        private int _index;
        public List<string> SentMessages { get; } = [];

        public event Action<string, BridgeStatusSnapshot>? StatusChanged;

        public Task<BridgeStatusSnapshot> EnsureConnectedAsync(AccountProfile account, CancellationToken cancellationToken = default)
        {
            var snap = new BridgeStatusSnapshot(BridgeConnectionState.Ready, "Ready");
            StatusChanged?.Invoke(account.Id, snap);
            return Task.FromResult(snap);
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
