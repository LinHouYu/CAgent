using System.Text;

namespace CAgent.Core.Protocol;

public static class OutputTruncator
{
    public static TruncatedOutput TruncateIfNeeded(
        string rawOutput,
        int maxChars,
        string? workspacePath = null)
    {
        rawOutput ??= string.Empty;
        var safeMax = Math.Max(400, maxChars);

        if (rawOutput.Length <= safeMax)
        {
            return new TruncatedOutput(rawOutput, WasTruncated: false, FullOutputLogPath: null);
        }

        var baseDir = !string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath)
            ? Path.Combine(workspacePath, ".cagent", "tool_outputs")
            : Path.Combine(Path.GetTempPath(), "CAgent", "tool_outputs");

        Directory.CreateDirectory(baseDir);
        var fileName = $"out_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..6]}.log";
        var fullLogPath = Path.Combine(baseDir, fileName);
        File.WriteAllText(fullLogPath, rawOutput, new UTF8Encoding(false));

        var totalLines = CountLines(rawOutput);
        var displayPath = !string.IsNullOrWhiteSpace(workspacePath) && fullLogPath.StartsWith(workspacePath, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(workspacePath, fullLogPath).Replace('\\', '/')
            : fullLogPath;

        var noticeTemplate =
            $"\n\n... [输出过长已截断：共 {rawOutput.Length} 字符 / {totalLines} 行，完整输出已保存至 `{displayPath}`，可用 `read_file` 按 startLine/endLine 分页读取] ...\n\n";

        var remaining = Math.Max(160, safeMax - noticeTemplate.Length);
        var headLen = remaining / 2;
        var tailLen = remaining - headLen;

        var truncated = string.Concat(
            rawOutput.AsSpan(0, headLen),
            noticeTemplate,
            rawOutput.AsSpan(rawOutput.Length - tailLen, tailLen));

        return new TruncatedOutput(truncated, WasTruncated: true, FullOutputLogPath: fullLogPath);
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        var count = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                count++;
            }
        }
        return count;
    }
}
