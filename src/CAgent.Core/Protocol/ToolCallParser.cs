using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CAgent.Core.Protocol;

public static partial class ToolCallParser
{
    private static readonly JsonDocumentOptions LenientJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    [GeneratedRegex(@"```(?:cagent|cagent-tool|json)?\s*\r?\n(?<body>\{[\s\S]*?\})\s*\r?\n?```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeFenceRegex();

    public static IReadOnlyList<ToolCallRequest> ParseToolCalls(string assistantMessage)
    {
        if (string.IsNullOrWhiteSpace(assistantMessage))
        {
            return [];
        }

        var results = new List<ToolCallRequest>();
        var matches = CodeFenceRegex().Matches(assistantMessage);

        foreach (Match match in matches)
        {
            var fenceHeader = match.Value;
            var isExplicitCagentFence = fenceHeader.StartsWith("```cagent", StringComparison.OrdinalIgnoreCase);
            var body = match.Groups["body"].Value.Trim();

            if (TryParseSingleToolJson(body, requireArgsWhenGenericJson: !isExplicitCagentFence, out var request) && request is not null)
            {
                results.Add(request);
            }
        }

        return results;
    }

    public static string BuildBootstrapSystemPrompt(string workspacePath)
    {
        var osName = OperatingSystem.IsWindows()
            ? "Windows (PowerShell UTF-8)"
            : OperatingSystem.IsMacOS()
                ? "macOS (bash/zsh)"
                : "Linux (bash)";

        return $$$"""
        [CAgent System Protocol]
        你已连接本地电脑操控代理 CAgent。
        当前系统: {{{osName}}}
        当前工作目录: {{{workspacePath}}}

        当你需要查看目录、读写文件、执行本地命令或查看屏幕时，请在回复末尾输出一个 `cagent` 代码块（每次调用 1 个工具，等待返回 `[CAgent Tool Result]` 后再继续下一步）：

        ```cagent
        {"tool": "工具名", "args": { ... }}
        ```

        可用工具列表：
        1. `list_dir` - 浏览目录: `{"tool":"list_dir","args":{"path":".","maxDepth":2}}`
        2. `read_file` - 读取文件: `{"tool":"read_file","args":{"path":"相对或绝对路径","startLine":1,"endLine":200}}`
        3. `write_file` - 写入文件: `{"tool":"write_file","args":{"path":"路径","content":"完整文本","append":false}}`
        4. `run_shell` - 执行终端命令: `{"tool":"run_shell","args":{"command":"命令内容"}}`
        5. `screenshot` - 截取当前电脑屏幕并以图片发给你: `{"tool":"screenshot","args":{}}`

        规则：
        - 优先使用 `read_file` / `write_file` 读写文本文件，避免用复杂的命令行转义写长文件。
        - 每次仅输出一个 `cagent` 代码块；任务彻底完成无需调用工具时，直接给出总结回复，不要包含 `cagent` 代码块。
        - 收到本条协议后，请先调用 `list_dir` 浏览当前工作目录，并简短告知我你已准备就绪。
        """;
    }

    public static string FormatToolResultEnvelope(
        string toolName,
        bool isSuccess,
        string content,
        IReadOnlyList<string>? attachmentPaths = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[CAgent Tool Result | tool={toolName} | status={(isSuccess ? "OK" : "ERROR")}]");
        if (attachmentPaths is { Count: > 0 })
        {
            var names = string.Join(", ", attachmentPaths.Select(Path.GetFileName));
            sb.AppendLine($"[已附带文件: {names}]");
        }
        sb.AppendLine(content.TrimEnd());
        sb.Append("[/CAgent Tool Result]");
        return sb.ToString();
    }

    private static bool TryParseSingleToolJson(
        string jsonText,
        bool requireArgsWhenGenericJson,
        out ToolCallRequest? request)
    {
        request = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonText, LenientJsonOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("tool", out var toolProp) || toolProp.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var toolName = toolProp.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return false;
            }

            var hasArgs = root.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object;
            if (requireArgsWhenGenericJson && !hasArgs)
            {
                return false;
            }

            var clonedArgs = hasArgs
                ? argsProp.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();

            request = new ToolCallRequest
            {
                ToolName = toolName,
                Arguments = clonedArgs,
                RawJson = jsonText
            };
            return true;
        }
        catch
        {
            return false;
        }
    }
}
