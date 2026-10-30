using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CAgent.Bridge.Cdp;

public sealed record ParsedStreamFrame(
    string? FullTextSnapshot,
    string? AppendedDelta,
    bool IsCompleted);

/// <summary>
/// Layer 1 Anti-Breakage Engine: Passively reads browser WebSocket / SignalR / SSE network frames
/// via CDP Network events without touching DOM selectors or forging requests.
/// </summary>
public sealed class NetworkStreamTap
{
    private const char SignalRRecordSeparator = '\u001e';

    private static readonly HashSet<string> IgnoredBotMessageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "InternalSearchQuery",
        "InternalSearchResult",
        "InternalLoaderMessage",
        "GenerateContentQuery",
        "RenderCardRequest",
        "SearchQuery",
        "Disengaged"
    };

    private static readonly HashSet<string> DoneEventNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "done",
        "turnComplete",
        "turn_complete",
        "message_stop",
        "completed",
        "response.completed",
        "final"
    };

    private readonly ConcurrentDictionary<string, string> _webSocketUrls = new();
    private readonly object _turnLock = new();
    private StringBuilder _accumulated = new();
    private bool _active;
    private TaskCompletionSource<string>? _turnCompletion;

    public event Action<string>? StreamingTextUpdated;

    public string CurrentText
    {
        get
        {
            lock (_turnLock)
            {
                return _accumulated.ToString();
            }
        }
    }

    public Task<string> BeginTurn()
    {
        lock (_turnLock)
        {
            _accumulated = new StringBuilder();
            _active = true;
            _turnCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _turnCompletion.Task;
        }
    }

    public void EndTurn()
    {
        lock (_turnLock)
        {
            _active = false;
            _turnCompletion?.TrySetCanceled();
        }
    }

    public void HandleCdpEvent(CdpEventMessage evt)
    {
        if (evt.Params.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (evt.Method)
        {
            case "Network.webSocketCreated":
            {
                if (evt.Params.TryGetProperty("requestId", out var idProp) &&
                    evt.Params.TryGetProperty("url", out var urlProp))
                {
                    var reqId = idProp.GetString();
                    var url = urlProp.GetString();
                    if (!string.IsNullOrEmpty(reqId) && !string.IsNullOrEmpty(url))
                    {
                        _webSocketUrls[reqId] = url;
                    }
                }
                break;
            }

            case "Network.webSocketFrameReceived":
            {
                if (!IsTurnActive())
                {
                    return;
                }

                if (evt.Params.TryGetProperty("response", out var resp) &&
                    resp.ValueKind == JsonValueKind.Object &&
                    resp.TryGetProperty("payloadData", out var payloadProp) &&
                    payloadProp.ValueKind == JsonValueKind.String)
                {
                    var payload = payloadProp.GetString();
                    if (!string.IsNullOrWhiteSpace(payload))
                    {
                        ProcessRawPayload(payload);
                    }
                }
                break;
            }

            case "Network.eventSourceMessageReceived":
            {
                if (!IsTurnActive())
                {
                    return;
                }

                var eventName = evt.Params.TryGetProperty("eventName", out var en) ? en.GetString() ?? "" : "";
                var data = evt.Params.TryGetProperty("data", out var dp) ? dp.GetString() ?? "" : "";
                ProcessSseMessage(eventName, data);
                break;
            }
        }
    }

    public void ProcessRawPayload(string rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return;
        }

        // SignalR frames (used by Microsoft 365 Work Copilot / Substrate) end with \u001e and may pack multiple records
        var segments = rawPayload.Contains(SignalRRecordSeparator)
            ? rawPayload.Split(SignalRRecordSeparator, StringSplitOptions.RemoveEmptyEntries)
            : [rawPayload];

        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            // Handle SSE lines if tunnelled over text frames
            if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in trimmed.Split('\n'))
                {
                    var l = line.Trim();
                    if (l.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        ProcessSseMessage("", l[5..].Trim());
                    }
                }
                continue;
            }

            var parsed = ParseJsonFrame(trimmed);
            ApplyParsedFrame(parsed);
        }
    }

    public void ProcessSseMessage(string eventName, string data)
    {
        if (string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase) ||
            DoneEventNames.Contains(eventName))
        {
            ApplyParsedFrame(new ParsedStreamFrame(null, null, IsCompleted: true));
            return;
        }

        if (!string.IsNullOrWhiteSpace(data) && data.TrimStart().StartsWith('{'))
        {
            var parsed = ParseJsonFrame(data.Trim());
            ApplyParsedFrame(parsed);
        }
    }

    public static ParsedStreamFrame ParseJsonFrame(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ParsedStreamFrame(null, null, false);
            }

            // 1. SignalR Ping / Handshake frames -> ignore
            if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.Number &&
                typeProp.TryGetInt32(out var signalRType))
            {
                if (signalRType is 6 or 7)
                {
                    return new ParsedStreamFrame(null, null, signalRType == 7);
                }

                // SignalR Type 1: Streaming Invocation (M365 Work Copilot / Sydney)
                if (signalRType == 1 && root.TryGetProperty("arguments", out var argsArr) && argsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var arg in argsArr.EnumerateArray())
                    {
                        var botText = ExtractBotMessagesText(arg);
                        if (botText is not null)
                        {
                            return new ParsedStreamFrame(FullTextSnapshot: botText, AppendedDelta: null, IsCompleted: false);
                        }
                    }
                }

                // SignalR Type 2: Final Completion Item (M365 Work Copilot / Sydney)
                if (signalRType == 2)
                {
                    string? finalBotText = null;
                    if (root.TryGetProperty("item", out var itemObj) && itemObj.ValueKind == JsonValueKind.Object)
                    {
                        finalBotText = ExtractBotMessagesText(itemObj);
                    }
                    return new ParsedStreamFrame(FullTextSnapshot: finalBotText, AppendedDelta: null, IsCompleted: true);
                }
            }

            // 2. Copilot Personal v2 WebSocket protocol: {"event": "appendText", "text": "..."} / {"event": "done"}
            if (root.TryGetProperty("event", out var evtProp) && evtProp.ValueKind == JsonValueKind.String)
            {
                var evtName = evtProp.GetString() ?? "";
                if (DoneEventNames.Contains(evtName))
                {
                    var maybeFull = TryGetString(root, "text");
                    return new ParsedStreamFrame(maybeFull, null, IsCompleted: true);
                }

                if (evtName.Equals("appendText", StringComparison.OrdinalIgnoreCase) ||
                    evtName.Equals("append", StringComparison.OrdinalIgnoreCase) ||
                    evtName.Equals("textDelta", StringComparison.OrdinalIgnoreCase))
                {
                    var delta = TryGetString(root, "text") ?? TryGetString(root, "delta");
                    return new ParsedStreamFrame(null, delta, IsCompleted: false);
                }

                if (evtName.Equals("replaceText", StringComparison.OrdinalIgnoreCase) ||
                    evtName.Equals("finalMessage", StringComparison.OrdinalIgnoreCase))
                {
                    var snap = TryGetString(root, "text") ?? TryGetString(root, "content");
                    return new ParsedStreamFrame(snap, null, IsCompleted: false);
                }
            }

            // 3. OpenAI / Graph / Generic SSE-JSON style: choices[0].delta.content or type="content_block_delta"
            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                string? deltaContent = null;
                if (firstChoice.TryGetProperty("delta", out var deltaObj) && deltaObj.ValueKind == JsonValueKind.Object)
                {
                    deltaContent = TryGetString(deltaObj, "content") ?? TryGetString(deltaObj, "text");
                }
                var finishReason = TryGetString(firstChoice, "finish_reason");
                var isStop = !string.IsNullOrEmpty(finishReason) && !string.Equals(finishReason, "null", StringComparison.OrdinalIgnoreCase);
                return new ParsedStreamFrame(null, deltaContent, isStop);
            }

            if (root.TryGetProperty("type", out var strTypeProp) && strTypeProp.ValueKind == JsonValueKind.String)
            {
                var tName = strTypeProp.GetString() ?? "";
                if (DoneEventNames.Contains(tName))
                {
                    return new ParsedStreamFrame(null, null, IsCompleted: true);
                }
                if (root.TryGetProperty("delta", out var dObj) && dObj.ValueKind == JsonValueKind.Object)
                {
                    var delta = TryGetString(dObj, "text") ?? TryGetString(dObj, "content");
                    if (delta is not null)
                    {
                        return new ParsedStreamFrame(null, delta, IsCompleted: false);
                    }
                }
            }

            return new ParsedStreamFrame(null, null, false);
        }
        catch
        {
            return new ParsedStreamFrame(null, null, false);
        }
    }

    private void ApplyParsedFrame(ParsedStreamFrame frame)
    {
        string? updatedSnapshot = null;
        TaskCompletionSource<string>? completedTcs = null;
        string finalResult = string.Empty;

        lock (_turnLock)
        {
            if (!_active)
            {
                return;
            }

            if (!string.IsNullOrEmpty(frame.FullTextSnapshot))
            {
                _accumulated.Clear();
                _accumulated.Append(frame.FullTextSnapshot);
                updatedSnapshot = _accumulated.ToString();
            }
            else if (!string.IsNullOrEmpty(frame.AppendedDelta))
            {
                _accumulated.Append(frame.AppendedDelta);
                updatedSnapshot = _accumulated.ToString();
            }

            if (frame.IsCompleted && _accumulated.Length > 0)
            {
                _active = false;
                finalResult = _accumulated.ToString();
                completedTcs = _turnCompletion;
            }
        }

        if (updatedSnapshot is not null)
        {
            StreamingTextUpdated?.Invoke(updatedSnapshot);
        }

        completedTcs?.TrySetResult(finalResult);
    }

    private bool IsTurnActive()
    {
        lock (_turnLock)
        {
            return _active;
        }
    }

    private static string? ExtractBotMessagesText(JsonElement container)
    {
        if (!container.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var msg in messages.EnumerateArray())
        {
            if (msg.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var author = TryGetString(msg, "author") ?? TryGetString(msg, "role");
            if (!string.IsNullOrEmpty(author) &&
                !author.Equals("bot", StringComparison.OrdinalIgnoreCase) &&
                !author.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var msgType = TryGetString(msg, "messageType");
            if (!string.IsNullOrEmpty(msgType) && IgnoredBotMessageTypes.Contains(msgType))
            {
                continue;
            }

            var text = TryGetString(msg, "text") ?? TryGetString(msg, "adaptiveCardsText");
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return parts.Count > 0 ? string.Join("\n\n", parts) : null;
    }

    private static string? TryGetString(JsonElement obj, string propName)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(propName, out var el) &&
            el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }
        return null;
    }
}
