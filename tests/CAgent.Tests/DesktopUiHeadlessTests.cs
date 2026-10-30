using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using CAgent.Core.Abstractions;
using CAgent.Core.Agent;
using CAgent.Core.Configuration;
using CAgent.Desktop;
using CAgent.Desktop.ViewModels;
using CAgent.Desktop.Views;
using CAgent.Tools;

[assembly: AvaloniaTestApplication(typeof(CAgent.Tests.HeadlessTestAppBuilder))]

namespace CAgent.Tests;

public sealed class HeadlessTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}

public sealed class DesktopUiHeadlessTests : IDisposable
{
    private readonly string _tempDir;

    public DesktopUiHeadlessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cagent_ui_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [AvaloniaFact]
    public void TopBarEditionSwitch_MultiAccountCreation_AndFullUiConfigPersistence_WorkAcrossRestarts()
    {
        var configPath = Path.Combine(_tempDir, "ui_config.json");
        string createdWorkAccountId;

        // Session 1: User opens window, switches to Work, adds a 2nd Work account, tweaks UI settings & resizes window
        using (var cfgSvc1 = new ConfigService(configPath))
        {
            var bridge1 = new NoOpBridge();
            var orch1 = new AgentOrchestrator(cfgSvc1, bridge1, BuiltInTools.CreateDefaultRegistry());
            var vm1 = new MainWindowViewModel(cfgSvc1, bridge1, orch1);
            var window1 = new MainWindow(cfgSvc1) { DataContext = vm1 };
            window1.Show();

            Assert.True(vm1.IsPersonalEditionActive);
            Assert.False(vm1.IsWorkEditionActive);

            // Switch to Work M365 via Top Bar command
            vm1.SwitchToWorkCommand.Execute(null);
            Assert.True(vm1.IsWorkEditionActive);
            Assert.Equal(CopilotEdition.Work, vm1.ActiveAccount.Edition);

            // Open Add Account drawer and create a second corporate account
            vm1.ToggleAddAccountPanelCommand.Execute(null);
            Assert.True(vm1.IsAddAccountPanelOpen);
            vm1.NewAccountName = "海外分公司 M365";
            vm1.NewAccountIsWork = true;
            vm1.ConfirmAddAccountCommand.Execute(null);

            Assert.False(vm1.IsAddAccountPanelOpen);
            Assert.Equal(3, vm1.Accounts.Count);
            Assert.Equal("海外分公司 M365", vm1.ActiveAccount.DisplayName);
            Assert.True(vm1.CanRemoveCurrentAccount);
            createdWorkAccountId = vm1.ActiveAccount.Id;

            // Modify UI preferences and window dimensions
            vm1.ToggleThemeCommand.Execute(null); // Dark -> Light
            vm1.ToggleSettingsCommand.Execute(null); // Expand settings drawer
            vm1.AutoApproveTools = true;
            vm1.MaxAutoSteps = 18;
            vm1.WorkspacePath = _tempDir;
            window1.Width = 1180;
            window1.Height = 790;

            window1.Close();
        }

        // Session 2: Reopen app from disk config.json and verify 100% UI state restoration
        using var cfgSvc2 = new ConfigService(configPath);
        var bridge2 = new NoOpBridge();
        var orch2 = new AgentOrchestrator(cfgSvc2, bridge2, BuiltInTools.CreateDefaultRegistry());
        var vm2 = new MainWindowViewModel(cfgSvc2, bridge2, orch2);
        var window2 = new MainWindow(cfgSvc2) { DataContext = vm2 };
        window2.Show();

        Assert.Equal(3, vm2.Accounts.Count);
        Assert.True(vm2.IsWorkEditionActive);
        Assert.Equal(createdWorkAccountId, vm2.ActiveAccount.Id);
        Assert.False(vm2.IsDarkMode);
        Assert.True(vm2.IsSettingsExpanded);
        Assert.True(vm2.AutoApproveTools);
        Assert.Equal(18m, vm2.MaxAutoSteps);
        Assert.Equal(_tempDir, vm2.WorkspacePath);
        Assert.Equal(1180, window2.Width);
        Assert.Equal(790, window2.Height);

        window2.Close();
    }

    [AvaloniaFact]
    public async Task TimelineApprovalCard_ExecutesFromViewModelAndIsolatesAccountHistories()
    {
        var configPath = Path.Combine(_tempDir, "ui_timeline.json");
        using var cfgSvc = new ConfigService(configPath);
        cfgSvc.Update(c => c.LastWorkspacePath = _tempDir);

        var bridge = new SingleToolMockBridge();
        var orch = new AgentOrchestrator(cfgSvc, bridge, BuiltInTools.CreateDefaultRegistry(), (_, _) => 0);
        var vm = new MainWindowViewModel(cfgSvc, bridge, orch);
        var window = new MainWindow(cfgSvc) { DataContext = vm };
        window.Show();

        vm.PromptInputText = "帮我创建一个测试文件";
        var sendTask = vm.SendPromptAsync();

        // Wait briefly for pending tool card to appear in TimelineItems
        for (var i = 0; i < 30 && !vm.TimelineItems.Any(t => t.IsPendingApproval); i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var toolCard = Assert.Single(vm.TimelineItems, t => t.IsPendingApproval);
        toolCard.ApproveCommand.Execute(null);

        await sendTask.WaitAsync(TimeSpan.FromSeconds(5));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(ToolApprovalStatus.ExecutedSuccess, toolCard.ApprovalStatus);

        // Switching to Work account shows clean isolated timeline, switching back restores Personal timeline!
        var personalTimelineCount = vm.TimelineItems.Count;
        Assert.True(personalTimelineCount >= 3);

        vm.SwitchToWorkCommand.Execute(null);
        Assert.Empty(vm.TimelineItems);

        vm.SwitchToPersonalCommand.Execute(null);
        Assert.Equal(personalTimelineCount, vm.TimelineItems.Count);

        window.Close();
    }

    [AvaloniaFact]
    public async Task MainWindow_RendersSkiaFrames_InDarkAndLightThemes()
    {
        var configPath = Path.Combine(_tempDir, "ui_render.json");
        using var cfgSvc = new ConfigService(configPath);
        cfgSvc.Update(c => c.LastWorkspacePath = @"C:\Projects\DemoWorkspace");

        var bridge = new SingleToolMockBridge();
        var orch = new AgentOrchestrator(cfgSvc, bridge, BuiltInTools.CreateDefaultRegistry(), (_, _) => 0);
        var app = Application.Current as App;
        var vm = new MainWindowViewModel(cfgSvc, bridge, orch, isDark => app?.ApplyTheme(isDark));
        var window = new MainWindow(cfgSvc) { DataContext = vm };
        window.Show();

        vm.PromptInputText = "帮我扫描当前工作目录并创建部署检查清单";
        _ = vm.SendPromptAsync();
        for (var i = 0; i < 30 && !vm.TimelineItems.Any(t => t.IsPendingApproval); i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        vm.IsSettingsExpanded = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        using var darkFrame = window.CaptureRenderedFrame();
        Assert.NotNull(darkFrame);
        Assert.True(darkFrame!.PixelSize.Width >= 900);

        var previewDir = Environment.GetEnvironmentVariable("CAGENT_UI_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(previewDir) && Directory.Exists(previewDir))
        {
            darkFrame.Save(Path.Combine(previewDir, "cagent_dark_ui.png"));
        }

        vm.SwitchToWorkCommand.Execute(null);
        vm.ToggleAddAccountPanelCommand.Execute(null);
        vm.NewAccountName = "企业研发部 M365";
        vm.IsDarkMode = false;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        using var lightFrame = window.CaptureRenderedFrame();
        Assert.NotNull(lightFrame);
        if (!string.IsNullOrWhiteSpace(previewDir) && Directory.Exists(previewDir))
        {
            lightFrame!.Save(Path.Combine(previewDir, "cagent_light_ui.png"));
        }

        window.Close();
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

    private sealed class NoOpBridge : IBrowserBridge
    {
        public event Action<string, BridgeStatusSnapshot>? StatusChanged;

        public Task<BridgeStatusSnapshot> EnsureConnectedAsync(AccountProfile account, CancellationToken cancellationToken = default)
        {
            var s = new BridgeStatusSnapshot(BridgeConnectionState.Ready, "已连接就绪");
            StatusChanged?.Invoke(account.Id, s);
            return Task.FromResult(s);
        }

        public Task<BridgeStatusSnapshot> CheckStatusAsync(AccountProfile account, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BridgeStatusSnapshot(BridgeConnectionState.Disconnected, "未启动"));

        public Task<string> SendAndAwaitReplyAsync(AccountProfile account, string message, IReadOnlyList<string>? attachmentPaths = null, Action<string>? onStreamingDelta = null, CancellationToken cancellationToken = default) =>
            Task.FromResult("OK");

        public Task<CalibratedSelector> CalibrateInputSelectorAsync(AccountProfile account, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CalibratedSelector { CssSelector = "#userInput" });

        public Task DisconnectAccountAsync(string accountId) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleToolMockBridge : IBrowserBridge
    {
        private int _turn;
        public event Action<string, BridgeStatusSnapshot>? StatusChanged { add { } remove { } }

        public Task<BridgeStatusSnapshot> EnsureConnectedAsync(AccountProfile account, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BridgeStatusSnapshot(BridgeConnectionState.Ready, "已就绪"));

        public Task<BridgeStatusSnapshot> CheckStatusAsync(AccountProfile account, CancellationToken cancellationToken = default) =>
            EnsureConnectedAsync(account, cancellationToken);

        public Task<string> SendAndAwaitReplyAsync(AccountProfile account, string message, IReadOnlyList<string>? attachmentPaths = null, Action<string>? onStreamingDelta = null, CancellationToken cancellationToken = default)
        {
            var reply = _turn++ == 0
                ? "```cagent\n{\"tool\":\"write_file\",\"args\":{\"path\":\"ui_test.txt\",\"content\":\"ok\"}}\n```"
                : "文件已创建！";
            onStreamingDelta?.Invoke(reply);
            return Task.FromResult(reply);
        }

        public Task<CalibratedSelector> CalibrateInputSelectorAsync(AccountProfile account, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CalibratedSelector { CssSelector = "#userInput" });

        public Task DisconnectAccountAsync(string accountId) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
