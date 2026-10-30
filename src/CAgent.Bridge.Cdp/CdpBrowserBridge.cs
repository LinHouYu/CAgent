using System.Collections.Concurrent;
using CAgent.Core.Abstractions;
using CAgent.Core.Adapters;
using CAgent.Core.Configuration;

namespace CAgent.Bridge.Cdp;

public sealed class CdpBrowserBridge : IBrowserBridge
{
    private sealed class AccountSession : IAsyncDisposable
    {
        public required PersistentCdpClient Client { get; init; }
        public required NetworkStreamTap StreamTap { get; init; }
        public string LastPageUrl { get; set; } = string.Empty;

        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private readonly ConfigService _configService;
    private readonly AdapterManager _adapterManager;
    private readonly BrowserProfileManager _profileManager;
    private readonly Func<AccountProfile, CancellationToken, Task<CdpTargetInfo>> _targetProvider;
    private readonly ConcurrentDictionary<string, AccountSession> _sessions = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public int ReplyTimeoutSeconds { get; set; } = 180;
    public int DomFallbackKickInMs { get; set; } = 2200;

    public event Action<string, BridgeStatusSnapshot>? StatusChanged;

    public CdpBrowserBridge(
        ConfigService configService,
        AdapterManager adapterManager,
        BrowserProfileManager? profileManager = null,
        Func<AccountProfile, CancellationToken, Task<CdpTargetInfo>>? customTargetProvider = null)
    {
        _configService = configService;
        _adapterManager = adapterManager;
        _profileManager = profileManager ?? new BrowserProfileManager(configService, adapterManager);
        _targetProvider = customTargetProvider ?? _profileManager.EnsureBrowserAndPageTargetAsync;
    }

    public async Task<BridgeStatusSnapshot> EnsureConnectedAsync(
        AccountProfile account,
        CancellationToken cancellationToken = default)
    {
        EmitStatus(account.Id, BridgeConnectionState.Launching, $"正在连接 {account.DisplayName} (端口 {account.DebugPort})...");

        try
        {
            var session = await GetOrConnectSessionAsync(account, cancellationToken).ConfigureAwait(false);
            var rule = _adapterManager.GetRule(account.Edition);
            var (probe, _) = await SemanticInputAndCalibrator.ProbeAndFocusInputAsync(
                session.Client,
                account,
                rule,
                focusAndClear: false,
                cancellationToken).ConfigureAwait(false);

            session.LastPageUrl = probe.PageUrl;

            if (probe.Found)
            {
                return EmitStatus(account.Id, BridgeConnectionState.Ready, "已连接就绪", probe.PageUrl);
            }

            if (probe.IsLoginPage)
            {
                return EmitStatus(account.Id, BridgeConnectionState.WaitingForLogin, "请在打开的浏览器窗口中登录 Copilot 账号", probe.PageUrl);
            }

            return EmitStatus(
                account.Id,
                BridgeConnectionState.WaitingForLogin,
                "已连接页面，等待进入 Copilot 聊天界面（如已打开可点击“校准输入框”）",
                probe.PageUrl);
        }
        catch (Exception ex)
        {
            return EmitStatus(account.Id, BridgeConnectionState.Error, $"连接失败: {ex.Message}");
        }
    }

    public async Task<BridgeStatusSnapshot> CheckStatusAsync(
        AccountProfile account,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(account.Id, out var session) || !session.Client.IsConnected)
        {
            var existingTarget = await _profileManager.TryGetExistingPageTargetAsync(account, cancellationToken).ConfigureAwait(false);
            if (existingTarget is null)
            {
                return EmitStatus(account.Id, BridgeConnectionState.Disconnected, "浏览器未启动");
            }
            return await EnsureConnectedAsync(account, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var rule = _adapterManager.GetRule(account.Edition);
            var (probe, _) = await SemanticInputAndCalibrator.ProbeAndFocusInputAsync(
                session.Client,
                account,
                rule,
                focusAndClear: false,
                cancellationToken).ConfigureAwait(false);

            if (probe.Found)
            {
                return EmitStatus(account.Id, BridgeConnectionState.Ready, "已连接就绪", probe.PageUrl);
            }

            return EmitStatus(
                account.Id,
                BridgeConnectionState.WaitingForLogin,
                probe.IsLoginPage ? "等待浏览器登录" : "未检测到聊天输入框",
                probe.PageUrl);
        }
        catch
        {
            await DisconnectAccountAsync(account.Id).ConfigureAwait(false);
            return EmitStatus(account.Id, BridgeConnectionState.Disconnected, "连接已断开");
        }
    }

    public async Task<string> SendAndAwaitReplyAsync(
        AccountProfile account,
        string message,
        IReadOnlyList<string>? attachmentPaths = null,
        Action<string>? onStreamingDelta = null,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrConnectSessionAsync(account, cancellationToken).ConfigureAwait(false);
        var rule = _adapterManager.GetRule(account.Edition);

        // Keep renderer active even when window is in background
        try
        {
            await session.Client.SendCommandAsync(
                "Emulation.setFocusEmulationEnabled",
                new { enabled = true },
                null,
                cancellationToken,
                timeoutMs: 3000).ConfigureAwait(false);
        }
        catch { }

        var (probe, matchedSessionId) = await SemanticInputAndCalibrator.ProbeAndFocusInputAsync(
            session.Client,
            account,
            rule,
            focusAndClear: true,
            cancellationToken).ConfigureAwait(false);

        if (!probe.Found)
        {
            var hint = probe.IsLoginPage
                ? "当前处于登录页，请先在浏览器中登录 Copilot 账号。"
                : "未找到 Copilot 聊天输入框，请确认页面已加载完成，或点击顶部「校准输入框」完成 3 秒自愈校准。";
            EmitStatus(account.Id, BridgeConnectionState.WaitingForLogin, hint, probe.PageUrl);
            throw new InvalidOperationException(hint);
        }

        EmitStatus(account.Id, BridgeConnectionState.Streaming, "正在发送消息并等待 Copilot 回复...", probe.PageUrl);

        // Arm both Layer 1 NetworkStreamTap AND DOM Mutation Fallback baseline BEFORE hitting Send
        await SemanticInputAndCalibrator.ArmDomFallbackBaselineAsync(session.Client, matchedSessionId, cancellationToken)
            .ConfigureAwait(false);

        var networkReplyTask = session.StreamTap.BeginTurn();
        void ForwardStream(string text) => onStreamingDelta?.Invoke(text);
        session.StreamTap.StreamingTextUpdated += ForwardStream;

        try
        {
            if (attachmentPaths is { Count: > 0 })
            {
                await SemanticInputAndCalibrator.AttachFilesAsync(
                    session.Client,
                    matchedSessionId,
                    rule,
                    attachmentPaths,
                    cancellationToken).ConfigureAwait(false);
            }

            await SemanticInputAndCalibrator.InjectTextAndSubmitAsync(
                session.Client,
                matchedSessionId,
                rule,
                message,
                cancellationToken).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ReplyTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var domFallbackTask = MonitorDomFallbackLoopAsync(
                session,
                matchedSessionId,
                networkReplyTask,
                onStreamingDelta,
                linkedCts.Token);

            var completed = await Task.WhenAny(networkReplyTask, domFallbackTask).ConfigureAwait(false);
            var finalReply = await completed.ConfigureAwait(false);

            EmitStatus(account.Id, BridgeConnectionState.Ready, "已连接就绪", probe.PageUrl);
            return finalReply;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            EmitStatus(account.Id, BridgeConnectionState.Error, "等待 Copilot 回复超时", probe.PageUrl);
            throw new TimeoutException($"等待 Copilot 回复超时 ({ReplyTimeoutSeconds}s)。");
        }
        finally
        {
            session.StreamTap.StreamingTextUpdated -= ForwardStream;
            session.StreamTap.EndTurn();
        }
    }

    public async Task<CalibratedSelector> CalibrateInputSelectorAsync(
        AccountProfile account,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrConnectSessionAsync(account, cancellationToken).ConfigureAwait(false);
        EmitStatus(account.Id, BridgeConnectionState.Calibrating, "请在浏览器中点击 Copilot 聊天输入框...");

        try
        {
            var calibrated = await SemanticInputAndCalibrator.RunInteractiveCalibrationAsync(
                session.Client,
                timeout,
                cancellationToken).ConfigureAwait(false);

            _configService.Update(cfg =>
            {
                var targetAcc = cfg.Accounts.FirstOrDefault(a => a.Id == account.Id);
                if (targetAcc is not null)
                {
                    targetAcc.CustomInputSelector = calibrated;
                }
            });
            account.CustomInputSelector = calibrated;

            EmitStatus(account.Id, BridgeConnectionState.Ready, $"校准成功: {calibrated.CssSelector}", session.LastPageUrl);
            return calibrated;
        }
        catch (Exception ex)
        {
            EmitStatus(account.Id, BridgeConnectionState.Error, $"校准未完成: {ex.Message}", session.LastPageUrl);
            throw;
        }
    }

    public async Task DisconnectAccountAsync(string accountId)
    {
        if (_sessions.TryRemove(accountId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            EmitStatus(accountId, BridgeConnectionState.Disconnected, "已断开连接");
        }
    }

    private async Task<AccountSession> GetOrConnectSessionAsync(AccountProfile account, CancellationToken ct)
    {
        if (_sessions.TryGetValue(account.Id, out var existing) && existing.Client.IsConnected)
        {
            return existing;
        }

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(account.Id, out existing))
            {
                if (existing.Client.IsConnected)
                {
                    return existing;
                }
                _sessions.TryRemove(account.Id, out _);
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            var target = await _targetProvider(account, ct).ConfigureAwait(false);
            var client = new PersistentCdpClient(target.WebSocketDebuggerUrl);
            var tap = new NetworkStreamTap();

            client.EventReceived += tap.HandleCdpEvent;
            client.Disconnected += () =>
            {
                EmitStatus(account.Id, BridgeConnectionState.Disconnected, "浏览器连接已关闭");
            };

            await client.ConnectAsync(ct).ConfigureAwait(false);

            var session = new AccountSession
            {
                Client = client,
                StreamTap = tap,
                LastPageUrl = target.Url
            };
            _sessions[account.Id] = session;
            return session;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<string> MonitorDomFallbackLoopAsync(
        AccountSession session,
        string? matchedSessionId,
        Task<string> networkReplyTask,
        Action<string>? onStreamingDelta,
        CancellationToken ct)
    {
        // Give Layer 1 NetworkStreamTap priority before polling DOM
        await Task.Delay(DomFallbackKickInMs, ct).ConfigureAwait(false);

        var lastText = string.Empty;
        var stableCycles = 0;

        while (!ct.IsCancellationRequested && !networkReplyTask.IsCompleted)
        {
            await Task.Delay(350, ct).ConfigureAwait(false);

            // If NetworkStreamTap already started receiving tokens, let NetworkStreamTap finish unless its socket went silent
            if (session.StreamTap.CurrentText.Length > 0)
            {
                continue;
            }

            var snap = await SemanticInputAndCalibrator.PollDomFallbackAsync(session.Client, matchedSessionId, ct)
                .ConfigureAwait(false);

            if (snap is null || string.IsNullOrWhiteSpace(snap.Text))
            {
                continue;
            }

            if (snap.Text != lastText)
            {
                lastText = snap.Text;
                stableCycles = 0;
                onStreamingDelta?.Invoke(lastText);
            }
            else if (!snap.IsGenerating)
            {
                stableCycles++;
                if (stableCycles >= 3) // ~1.05s stable & Stop button gone
                {
                    return lastText;
                }
            }
        }

        return await networkReplyTask.ConfigureAwait(false);
    }

    private BridgeStatusSnapshot EmitStatus(
        string accountId,
        BridgeConnectionState state,
        string message,
        string? pageUrl = null)
    {
        var snap = new BridgeStatusSnapshot(state, message, pageUrl);
        StatusChanged?.Invoke(accountId, snap);
        return snap;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _sessions)
        {
            await kvp.Value.DisposeAsync().ConfigureAwait(false);
        }
        _sessions.Clear();
        _connectLock.Dispose();
    }
}
