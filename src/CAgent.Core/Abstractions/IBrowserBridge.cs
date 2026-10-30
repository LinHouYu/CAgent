using CAgent.Core.Configuration;

namespace CAgent.Core.Abstractions;

public enum BridgeConnectionState
{
    Disconnected,
    Launching,
    WaitingForLogin,
    Ready,
    Streaming,
    Calibrating,
    Error
}

public sealed record BridgeStatusSnapshot(
    BridgeConnectionState State,
    string Message,
    string? CurrentPageUrl = null);

public interface IBrowserBridge : IAsyncDisposable
{
    event Action<string, BridgeStatusSnapshot>? StatusChanged;

    Task<BridgeStatusSnapshot> EnsureConnectedAsync(
        AccountProfile account,
        CancellationToken cancellationToken = default);

    Task<BridgeStatusSnapshot> CheckStatusAsync(
        AccountProfile account,
        CancellationToken cancellationToken = default);

    Task<string> SendAndAwaitReplyAsync(
        AccountProfile account,
        string message,
        IReadOnlyList<string>? attachmentPaths = null,
        Action<string>? onStreamingDelta = null,
        CancellationToken cancellationToken = default);

    Task<CalibratedSelector> CalibrateInputSelectorAsync(
        AccountProfile account,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task DisconnectAccountAsync(string accountId);
}
