using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CAgent.Bridge.Cdp;

public sealed record CdpEventMessage(
    string Method,
    JsonElement Params,
    string? SessionId);

public sealed record ChildTargetSession(
    string SessionId,
    string TargetId,
    string Type,
    string Url);

public sealed class PersistentCdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingCalls = new();
    private readonly ConcurrentDictionary<string, ChildTargetSession> _childSessions = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveLoopCts = new();
    private Task? _receiveLoopTask;
    private int _nextId;
    private int _disposed;

    public string WebSocketUrl { get; }
    public bool IsConnected => _ws.State == WebSocketState.Open && _disposed == 0;
    public IReadOnlyCollection<ChildTargetSession> ChildSessions => _childSessions.Values.ToArray();

    public event Action<CdpEventMessage>? EventReceived;
    public event Action? Disconnected;

    public PersistentCdpClient(string webSocketUrl)
    {
        WebSocketUrl = webSocketUrl;
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _ws.ConnectAsync(new Uri(WebSocketUrl), cancellationToken).ConfigureAwait(false);
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_receiveLoopCts.Token));

        // Enable core domains on root page target
        await SendCommandAsync("Page.enable", null, null, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync("Runtime.enable", null, null, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync("Network.enable", null, null, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync("DOM.enable", null, null, cancellationToken).ConfigureAwait(false);

        // Auto-attach out-of-process iframes (crucial for Microsoft 365 Work Copilot!)
        try
        {
            await SendCommandAsync(
                "Target.setAutoAttach",
                new { autoAttach = true, waitForDebuggerOnStart = false, flatten = true },
                null,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ignore if mock/older target does not support Target.setAutoAttach
        }
    }

    public async Task<JsonElement> SendCommandAsync(
        string method,
        object? parameters = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default,
        int timeoutMs = 10_000)
    {
        if (!IsConnected)
        {
            throw new WebSocketException("CDP WebSocket 连接未处于打开状态。");
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCalls[id] = tcs;

        try
        {
            var payload = BuildCommandJson(id, method, parameters, sessionId);
            var bytes = Encoding.UTF8.GetBytes(payload);

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await using var reg = linked.Token.Register(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                else
                {
                    tcs.TrySetException(new TimeoutException($"CDP 指令 '{method}' 等待响应超时 ({timeoutMs}ms)。"));
                }
            });

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingCalls.TryRemove(id, out _);
        }
    }

    public async Task<T?> EvaluateJsAsync<T>(
        string expression,
        string? sessionId = null,
        bool awaitPromise = false,
        CancellationToken cancellationToken = default,
        int timeoutMs = 8_000)
    {
        var res = await SendCommandAsync(
            "Runtime.evaluate",
            new
            {
                expression,
                returnByValue = true,
                awaitPromise
            },
            sessionId,
            cancellationToken,
            timeoutMs).ConfigureAwait(false);

        if (res.ValueKind == JsonValueKind.Object &&
            res.TryGetProperty("result", out var resultObj) &&
            resultObj.ValueKind == JsonValueKind.Object &&
            resultObj.TryGetProperty("value", out var val))
        {
            if (val.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return default;
            }
            return val.Deserialize<T>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        return default;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    if (result.Count > 0)
                    {
                        ms.Write(buffer, 0, result.Count);
                    }
                } while (!result.EndOfMessage);

                if (ms.Length > 0)
                {
                    DispatchRawMessage(ms.GetBuffer().AsSpan(0, (int)ms.Length));
                }
            }
        }
        catch
        {
            // Socket closed or cancelled
        }
        finally
        {
            foreach (var kvp in _pendingCalls)
            {
                kvp.Value.TrySetException(new WebSocketException("CDP WebSocket 连接已断开。"));
            }
            _pendingCalls.Clear();
            Disconnected?.Invoke();
        }
    }

    private void DispatchRawMessage(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
            {
                if (_pendingCalls.TryGetValue(id, out var tcs))
                {
                    if (root.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.Object)
                    {
                        var errMsg = errProp.TryGetProperty("message", out var m) ? m.GetString() : errProp.GetRawText();
                        tcs.TrySetException(new InvalidOperationException($"CDP Error: {errMsg}"));
                    }
                    else if (root.TryGetProperty("result", out var resultProp))
                    {
                        tcs.TrySetResult(resultProp.Clone());
                    }
                    else
                    {
                        tcs.TrySetResult(default);
                    }
                }
                return;
            }

            if (root.TryGetProperty("method", out var methodProp) && methodProp.ValueKind == JsonValueKind.String)
            {
                var method = methodProp.GetString()!;
                var sessionId = root.TryGetProperty("sessionId", out var sidProp) && sidProp.ValueKind == JsonValueKind.String
                    ? sidProp.GetString()
                    : null;
                var @params = root.TryGetProperty("params", out var pProp)
                    ? pProp.Clone()
                    : default;

                HandleTargetLifecycleEvents(method, @params);
                EventReceived?.Invoke(new CdpEventMessage(method, @params, sessionId));
            }
        }
        catch
        {
            // Ignore malformed telemetry frame
        }
    }

    private void HandleTargetLifecycleEvents(string method, JsonElement @params)
    {
        if (@params.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (method == "Target.attachedToTarget" &&
            @params.TryGetProperty("sessionId", out var sidProp) &&
            @params.TryGetProperty("targetInfo", out var tInfo))
        {
            var sid = sidProp.GetString() ?? "";
            var tid = tInfo.TryGetProperty("targetId", out var idEl) ? idEl.GetString() ?? "" : "";
            var type = tInfo.TryGetProperty("type", out var tpEl) ? tpEl.GetString() ?? "" : "";
            var url = tInfo.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "";

            if (!string.IsNullOrEmpty(sid) && type is "iframe" or "page")
            {
                _childSessions[sid] = new ChildTargetSession(sid, tid, type, url);
                // Fire-and-forget enabling Runtime/Network/DOM inside attached iframe
                _ = EnableChildIframeSessionAsync(sid);
            }
        }
        else if (method == "Target.detachedFromTarget" &&
                 @params.TryGetProperty("sessionId", out var detachedSid))
        {
            var sid = detachedSid.GetString();
            if (!string.IsNullOrEmpty(sid))
            {
                _childSessions.TryRemove(sid, out _);
            }
        }
    }

    private async Task EnableChildIframeSessionAsync(string sessionId)
    {
        try
        {
            await SendCommandAsync("Runtime.enable", null, sessionId, timeoutMs: 3000).ConfigureAwait(false);
            await SendCommandAsync("Network.enable", null, sessionId, timeoutMs: 3000).ConfigureAwait(false);
            await SendCommandAsync("DOM.enable", null, sessionId, timeoutMs: 3000).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort for child frames
        }
    }

    private static string BuildCommandJson(int id, string method, object? parameters, string? sessionId)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (parameters is not null)
            {
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters);
            }
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                writer.WriteString("sessionId", sessionId);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _receiveLoopCts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(1000);
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore close errors
        }

        if (_receiveLoopTask is not null)
        {
            try { await _receiveLoopTask.ConfigureAwait(false); } catch { }
        }

        _ws.Dispose();
        _sendLock.Dispose();
        _receiveLoopCts.Dispose();
    }
}
