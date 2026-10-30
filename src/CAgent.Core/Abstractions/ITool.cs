using CAgent.Core.Protocol;

namespace CAgent.Core.Abstractions;

public sealed record ToolExecutionContext(
    string WorkspacePath,
    TimeSpan CommandTimeout,
    CancellationToken CancellationToken = default);

public sealed record ToolExecutionResult(
    bool IsSuccess,
    string Output,
    IReadOnlyList<string>? AttachmentPaths = null)
{
    public static ToolExecutionResult Ok(string output, IReadOnlyList<string>? attachments = null) =>
        new(true, output, attachments ?? []);

    public static ToolExecutionResult Fail(string error) =>
        new(false, error, []);
}

public interface ITool
{
    string Name { get; }
    bool IsMutating { get; }
    Task<ToolExecutionResult> ExecuteAsync(ToolCallRequest request, ToolExecutionContext context);
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        foreach (var tool in tools)
        {
            _tools[tool.Name] = tool;
        }
    }

    public bool TryGetTool(string name, out ITool? tool) => _tools.TryGetValue(name, out tool);

    public IReadOnlyCollection<ITool> All => _tools.Values;

    public async Task<ToolExecutionResult> ExecuteAsync(ToolCallRequest request, ToolExecutionContext context)
    {
        if (!_tools.TryGetValue(request.ToolName, out var tool))
        {
            var available = string.Join(", ", _tools.Keys.OrderBy(k => k));
            return ToolExecutionResult.Fail($"未知工具 '{request.ToolName}'。可用工具: {available}");
        }

        try
        {
            return await tool.ExecuteAsync(request, context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"工具 '{request.ToolName}' 执行异常: {ex.Message}");
        }
    }
}
