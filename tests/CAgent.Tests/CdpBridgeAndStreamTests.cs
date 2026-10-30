using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CAgent.Bridge.Cdp;
using CAgent.Core.Abstractions;
using CAgent.Core.Adapters;
using CAgent.Core.Configuration;

namespace CAgent.Tests;

public sealed class CdpBridgeAndStreamTests : IDisposable
{
    private readonly string _tempDir;

    public CdpBridgeAndStreamTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cagent_cdp_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task NetworkStreamTap_ParsesPersonalCopilotWebSocketStream_ZeroPolling()
    {
        var tap = new NetworkStreamTap();
        var deltas = new List<string>();
        tap.StreamingTextUpdated += text => deltas.Add(text);

        var turnTask = tap.BeginTurn();

        tap.HandleCdpEvent(MakeWsFrameEvent("""{"event":"appendText","text":"你好，"}"""));
        tap.HandleCdpEvent(MakeWsFrameEvent("""{"event":"appendText","text":"正在扫描目录：\n```cagent\n{\"tool\":\"list_dir\",\"args\":{\"path\":\".\"}}\n```"}"""));
        Assert.False(turnTask.IsCompleted);

        tap.HandleCdpEvent(MakeWsFrameEvent("""{"event":"done"}"""));
        var finalText = await turnTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains("你好，正在扫描目录：", finalText);
        Assert.Contains("\"tool\":\"list_dir\"", finalText);
        Assert.Equal(2, deltas.Count);
    }

    [Fact]
    public async Task NetworkStreamTap_ParsesWorkCopilotM365SignalRFrames_AndIgnoresInternalSearch()
    {
        var tap = new NetworkStreamTap();
        var turnTask = tap.BeginTurn();

        // Ping + internal search thought packed in a single SignalR frame (\u001e)
        var frame1 =
            "{\"type\":6}\u001e" +
            "{\"type\":1,\"target\":\"update\",\"arguments\":[{\"messages\":[{\"author\":\"bot\",\"messageType\":\"InternalSearchQuery\",\"text\":\"searching enterprise graph...\"}]}]}\u001e";
        tap.ProcessRawPayload(frame1);
        Assert.Equal(string.Empty, tap.CurrentText);

        // Partial bot markdown update
        var frame2 =
            "{\"type\":1,\"target\":\"update\",\"arguments\":[{\"messages\":[{\"author\":\"bot\",\"text\":\"已连接企业版 Copilot，准备执行...\"}]}]}\u001e";
        tap.ProcessRawPayload(frame2);
        Assert.Equal("已连接企业版 Copilot，准备执行...", tap.CurrentText);
        Assert.False(turnTask.IsCompleted);

        // Final Type 2 SignalR completion item
        var frame3 =
            "{\"type\":2,\"invocationId\":\"0\",\"item\":{\"messages\":[" +
            "{\"author\":\"bot\",\"messageType\":\"InternalSearchQuery\",\"text\":\"ignored\"}," +
            "{\"author\":\"bot\",\"text\":\"已连接企业版 Copilot，准备执行：\\n```cagent\\n{\\\"tool\\\":\\\"list_dir\\\",\\\"args\\\":{}}\\n```\"}" +
            "]}}\u001e";
        tap.ProcessRawPayload(frame3);

        var completed = await turnTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("ignored", completed);
        Assert.Contains("已连接企业版 Copilot", completed);
        Assert.Contains("\"tool\":\"list_dir\"", completed);
    }

    [Fact]
    public async Task CdpBrowserBridge_EndToEndOverLoopbackWebSocket_SupportsChildIframeSendStreamAndCalibration()
    {
        await using var mockCdp = new LoopbackMockCdpServer();
        await mockCdp.StartAsync();

        var configPath = Path.Combine(_tempDir, "config.json");
        using var configSvc = new ConfigService(configPath);
        var adapterMgr = new AdapterManager(Path.Combine(_tempDir, "adapters.json"));

        var workAccount = configSvc.Current.SwitchEdition(CopilotEdition.Work);

        await using var bridge = new CdpBrowserBridge(
            configSvc,
            adapterMgr,
            customTargetProvider: (_, _) => Task.FromResult(new CdpTargetInfo
            {
                Id = "page-1",
                Type = "page",
                Url = "https://m365.cloud.microsoft/chat",
                WebSocketDebuggerUrl = mockCdp.WebSocketUrl
            }));

        // 1. Connect and verify Ready state (input discovered inside attached M365 child iframe!)
        var status = await bridge.EnsureConnectedAsync(workAccount);
        Assert.Equal(BridgeConnectionState.Ready, status.State);

        // 2. Send prompt and receive streamed SignalR reply over persistent single WebSocket
        var streamedUpdates = new List<string>();
        var reply = await bridge.SendAndAwaitReplyAsync(
            workAccount,
            "帮我检查工作目录",
            attachmentPaths: null,
            onStreamingDelta: s => streamedUpdates.Add(s));

        Assert.Equal("帮我检查工作目录", mockCdp.LastInsertedText);
        Assert.Contains("收到指令：帮我检查工作目录", reply);
        Assert.NotEmpty(streamedUpdates);

        // 3. Test 3-Second Click-to-Calibrate self-healing persistence
        var calibrated = await bridge.CalibrateInputSelectorAsync(workAccount, TimeSpan.FromSeconds(5));
        Assert.Equal("div[data-testid=\"m365-calibrated-editor\"]", calibrated.CssSelector);
        Assert.Equal("div[data-testid=\"m365-calibrated-editor\"]", configSvc.Current.GetActiveAccount().CustomInputSelector?.CssSelector);
    }

    [Fact]
    public async Task CdpBrowserBridge_FallsBackToDomObserver_WhenNetworkStreamIsSilent()
    {
        await using var mockCdp = new LoopbackMockCdpServer { DisableNetworkFrames = true };
        await mockCdp.StartAsync();

        var configPath = Path.Combine(_tempDir, "config_dom.json");
        using var configSvc = new ConfigService(configPath);
        var adapterMgr = new AdapterManager();
        var personalAccount = configSvc.Current.SwitchEdition(CopilotEdition.Personal);

        await using var bridge = new CdpBrowserBridge(
            configSvc,
            adapterMgr,
            customTargetProvider: (_, _) => Task.FromResult(new CdpTargetInfo
            {
                Id = "page-2",
                Type = "page",
                Url = "https://copilot.microsoft.com",
                WebSocketDebuggerUrl = mockCdp.WebSocketUrl
            }))
        {
            DomFallbackKickInMs = 100
        };

        var reply = await bridge.SendAndAwaitReplyAsync(personalAccount, "测试 DOM 降级兜底");
        Assert.Equal("来自 DOM 降级观察器的完整回复", reply);
    }

    [Fact]
    public async Task StartNewConversationAsync_ClicksNewChatButtonOverLoopbackCdp()
    {
        await using var mockCdp = new LoopbackMockCdpServer();
        await mockCdp.StartAsync();

        var configPath = Path.Combine(_tempDir, "config_newchat.json");
        using var configSvc = new ConfigService(configPath);
        var adapterMgr = new AdapterManager(Path.Combine(_tempDir, "adapters_newchat.json"));
        var personalAccount = configSvc.Current.SwitchEdition(CopilotEdition.Personal);

        await using var bridge = new CdpBrowserBridge(
            configSvc,
            adapterMgr,
            customTargetProvider: (_, _) => Task.FromResult(new CdpTargetInfo
            {
                Id = "page-3",
                Type = "page",
                Url = "https://copilot.microsoft.com",
                WebSocketDebuggerUrl = mockCdp.WebSocketUrl
            }));

        var snap = await bridge.StartNewConversationAsync(personalAccount);
        Assert.Equal(BridgeConnectionState.Ready, snap.State);
        Assert.True(mockCdp.NewChatButtonClicked);
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

    private static CdpEventMessage MakeWsFrameEvent(string payloadData)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            requestId = "ws-1",
            timestamp = 1.0,
            response = new { opcode = 1, mask = false, payloadData }
        }));
        return new CdpEventMessage("Network.webSocketFrameReceived", doc.RootElement.Clone(), null);
    }

    private sealed class LoopbackMockCdpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private Task? _acceptTask;

        public string WebSocketUrl { get; private set; } = string.Empty;
        public string LastInsertedText { get; private set; } = string.Empty;
        public bool DisableNetworkFrames { get; set; }
        public bool NewChatButtonClicked { get; private set; }

        public Task StartAsync()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            WebSocketUrl = $"ws://127.0.0.1:{port}/devtools/page/mock";
            _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
                _ = Task.Run(() => HandleClientAsync(tcp, ct), ct);
            }
        }

        private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
        {
            using (tcp)
            await using (var stream = tcp.GetStream())
            {
                // Perform standard RFC 6455 HTTP -> WebSocket upgrade
                using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                string? wsKey = null;
                while (true)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line))
                    {
                        break;
                    }
                    if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    {
                        wsKey = line["Sec-WebSocket-Key:".Length..].Trim();
                    }
                }

                var acceptKey = Convert.ToBase64String(
                    SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

                var handshake =
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Upgrade: websocket\r\n" +
                    $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";

                var headerBytes = Encoding.ASCII.GetBytes(handshake);
                await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);

                using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
                var buf = new byte[64 * 1024];

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                        if (res.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }
                        ms.Write(buf, 0, res.Count);
                    } while (!res.EndOfMessage);

                    using var doc = JsonDocument.Parse(ms.ToArray());
                    var root = doc.RootElement;
                    var id = root.GetProperty("id").GetInt32();
                    var method = root.GetProperty("method").GetString() ?? "";
                    var sessionId = root.TryGetProperty("sessionId", out var sidProp) ? sidProp.GetString() : null;

                    if (method == "Target.setAutoAttach")
                    {
                        await SendJsonAsync(ws, new { id, result = new { } }, ct);
                        // Simulate M365 out-of-process child iframe attachment!
                        await SendJsonAsync(ws, new
                        {
                            method = "Target.attachedToTarget",
                            @params = new
                            {
                                sessionId = "iframe-session-m365",
                                targetInfo = new
                                {
                                    targetId = "child-1",
                                    type = "iframe",
                                    url = "https://substrate.office.com/m365Copilot/chat"
                                }
                            }
                        }, ct);
                        continue;
                    }

                    if (method == "Input.insertText")
                    {
                        LastInsertedText = root.GetProperty("params").GetProperty("text").GetString() ?? "";
                        await SendJsonAsync(ws, new { id, sessionId, result = new { } }, ct);
                        continue;
                    }

                    if (method == "Runtime.evaluate")
                    {
                        var expr = root.GetProperty("params").GetProperty("expression").GetString() ?? "";
                        object? evalValue = HandleEvaluate(expr, sessionId);

                        await SendJsonAsync(ws, new
                        {
                            id,
                            sessionId,
                            result = new { result = new { type = "object", value = evalValue } }
                        }, ct);

                        // If send button was clicked and Network frames are enabled, emit SignalR frames!
                        if (expr.Contains("btn.click()") && !DisableNetworkFrames)
                        {
                            var deltaPayload =
                                $"{{\"type\":1,\"arguments\":[{{\"messages\":[{{\"author\":\"bot\",\"text\":\"收到指令：{LastInsertedText}\"}}]}}]}}\u001e";
                            var donePayload =
                                $"{{\"type\":2,\"item\":{{\"messages\":[{{\"author\":\"bot\",\"text\":\"收到指令：{LastInsertedText}，执行完毕。\"}}]}}}}\u001e";

                            await SendJsonAsync(ws, new
                            {
                                method = "Network.webSocketFrameReceived",
                                sessionId = "iframe-session-m365",
                                @params = new { requestId = "1", response = new { payloadData = deltaPayload } }
                            }, ct);

                            await SendJsonAsync(ws, new
                            {
                                method = "Network.webSocketFrameReceived",
                                sessionId = "iframe-session-m365",
                                @params = new { requestId = "1", response = new { payloadData = donePayload } }
                            }, ct);
                        }
                        continue;
                    }

                    await SendJsonAsync(ws, new { id, sessionId, result = new { } }, ct);
                }
            }
        }

        private object? HandleEvaluate(string expr, string? sessionId)
        {
            if (expr.Contains("newChatEl.click()"))
            {
                NewChatButtonClicked = true;
                return true;
            }

            // Calibration promise
            if (expr.Contains("__cagent_calibrator_banner"))
            {
                return new
                {
                    cssSelector = "div[data-testid=\"m365-calibrated-editor\"]",
                    tagName = "div",
                    ariaLabel = "Ask Copilot",
                    placeholder = "",
                    role = "textbox",
                    frameUrlHint = "substrate.office.com/m365Copilot/chat"
                };
            }

            // Probe script: root page has no input, child iframe has the chat input!
            if (expr.Contains("isLoginPage"))
            {
                if (sessionId == "iframe-session-m365")
                {
                    return new
                    {
                        found = true,
                        isLoginPage = false,
                        pageUrl = "https://substrate.office.com/m365Copilot/chat",
                        tagName = "div",
                        score = 950
                    };
                }
                return new
                {
                    found = true,
                    isLoginPage = false,
                    pageUrl = "https://copilot.microsoft.com",
                    tagName = "textarea",
                    score = 500
                };
            }

            if (expr.Contains("btn.click()"))
            {
                return true;
            }

            if (expr.Contains("isGenerating"))
            {
                return new { text = "来自 DOM 降级观察器的完整回复", isGenerating = false };
            }

            return true;
        }

        private static async Task SendJsonAsync(WebSocket ws, object payload, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            if (_acceptTask is not null)
            {
                try { await _acceptTask.ConfigureAwait(false); } catch { }
            }
            _cts.Dispose();
        }
    }
}
