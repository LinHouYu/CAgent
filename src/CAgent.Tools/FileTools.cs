using System.Text;
using CAgent.Core.Abstractions;
using CAgent.Core.Protocol;

namespace CAgent.Tools;

internal static class PathResolver
{
    public static string ResolveWorkspacePath(string workspacePath, string inputPath)
    {
        var baseDir = string.IsNullOrWhiteSpace(workspacePath)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workspacePath);

        if (string.IsNullOrWhiteSpace(inputPath) || inputPath.Trim() == ".")
        {
            return baseDir;
        }

        var normalized = inputPath.Trim()
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        return Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(baseDir, normalized));
    }
}

public sealed class ListDirTool : ITool
{
    private static readonly HashSet<string> IgnoredDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", ".cagent", "__pycache__"
    };

    public string Name => "list_dir";
    public bool IsMutating => false;

    public Task<ToolExecutionResult> ExecuteAsync(ToolCallRequest request, ToolExecutionContext context)
    {
        var targetPath = PathResolver.ResolveWorkspacePath(context.WorkspacePath, request.GetString("path", "."));
        var maxDepth = Math.Clamp(request.GetInt("maxDepth", 2), 1, 5);
        var maxEntries = Math.Clamp(request.GetInt("maxEntries", 200), 10, 1000);

        if (!Directory.Exists(targetPath))
        {
            return Task.FromResult(ToolExecutionResult.Fail($"目录不存在: {targetPath}"));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"目录: {targetPath}");

        var count = 0;
        var hitLimit = false;
        TraverseDirectory(targetPath, targetPath, 1, maxDepth, maxEntries, sb, ref count, ref hitLimit);

        if (count == 0)
        {
            sb.AppendLine("(空目录)");
        }
        else if (hitLimit)
        {
            sb.AppendLine($"... [已达到单次展示上限 {maxEntries} 项，请指定具体子目录进一步查看]");
        }

        return Task.FromResult(ToolExecutionResult.Ok(sb.ToString()));
    }

    private static void TraverseDirectory(
        string rootDir,
        string currentDir,
        int currentDepth,
        int maxDepth,
        int maxEntries,
        StringBuilder sb,
        ref int count,
        ref bool hitLimit)
    {
        if (hitLimit)
        {
            return;
        }

        IEnumerable<string> subDirs;
        IEnumerable<string> files;

        try
        {
            subDirs = Directory.EnumerateDirectories(currentDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
            files = Directory.EnumerateFiles(currentDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            sb.AppendLine($"{new string(' ', (currentDepth - 1) * 2)}[无权限访问] {Path.GetFileName(currentDir)}/");
            return;
        }

        var indent = new string(' ', (currentDepth - 1) * 2);

        foreach (var dir in subDirs)
        {
            var dirName = Path.GetFileName(dir);
            if (IgnoredDirNames.Contains(dirName))
            {
                continue;
            }

            if (count >= maxEntries)
            {
                hitLimit = true;
                return;
            }

            count++;
            sb.AppendLine($"{indent}[DIR]  {dirName}/");

            if (currentDepth < maxDepth)
            {
                TraverseDirectory(rootDir, dir, currentDepth + 1, maxDepth, maxEntries, sb, ref count, ref hitLimit);
                if (hitLimit)
                {
                    return;
                }
            }
        }

        foreach (var file in files)
        {
            if (count >= maxEntries)
            {
                hitLimit = true;
                return;
            }

            count++;
            var info = new FileInfo(file);
            sb.AppendLine($"{indent}[FILE] {info.Name} ({FormatBytes(info.Length)})");
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}

public sealed class ReadFileTool : ITool
{
    public string Name => "read_file";
    public bool IsMutating => false;

    public async Task<ToolExecutionResult> ExecuteAsync(ToolCallRequest request, ToolExecutionContext context)
    {
        var rawPath = request.GetString("path");
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return ToolExecutionResult.Fail("缺少必填参数 'path'。");
        }

        var fullPath = PathResolver.ResolveWorkspacePath(context.WorkspacePath, rawPath);
        if (!File.Exists(fullPath))
        {
            return ToolExecutionResult.Fail($"文件不存在: {fullPath}");
        }

        // Quick binary file check on first 4KB
        await using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var sampleLen = (int)Math.Min(4096, fs.Length);
            var sample = new byte[sampleLen];
            var read = await fs.ReadAsync(sample.AsMemory(0, sampleLen), context.CancellationToken).ConfigureAwait(false);
            for (var i = 0; i < read; i++)
            {
                if (sample[i] == 0)
                {
                    return ToolExecutionResult.Fail($"文件 '{fullPath}' 疑似二进制文件 ({fs.Length} 字节)，不支持按文本读取。");
                }
            }
        }

        var allLines = await File.ReadAllLinesAsync(fullPath, Encoding.UTF8, context.CancellationToken).ConfigureAwait(false);
        var totalLines = allLines.Length;

        if (totalLines == 0)
        {
            return ToolExecutionResult.Ok($"文件: {fullPath} (空文件, 0 行)");
        }

        var startLine = Math.Clamp(request.GetInt("startLine", 1), 1, totalLines);
        var defaultEnd = Math.Min(totalLines, startLine + 249);
        var endLine = Math.Clamp(request.GetInt("endLine", defaultEnd), startLine, totalLines);

        var sb = new StringBuilder();
        sb.AppendLine($"文件: {fullPath} (显示第 {startLine}-{endLine} 行，共 {totalLines} 行)");
        for (var i = startLine; i <= endLine; i++)
        {
            sb.AppendLine($"{i,4}: {allLines[i - 1]}");
        }

        return ToolExecutionResult.Ok(sb.ToString());
    }
}

public sealed class WriteFileTool : ITool
{
    public string Name => "write_file";
    public bool IsMutating => true;

    public async Task<ToolExecutionResult> ExecuteAsync(ToolCallRequest request, ToolExecutionContext context)
    {
        var rawPath = request.GetString("path");
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return ToolExecutionResult.Fail("缺少必填参数 'path'。");
        }

        var content = request.GetString("content");
        var append = request.GetBool("append", defaultValue: false);
        var fullPath = PathResolver.ResolveWorkspacePath(context.WorkspacePath, rawPath);

        var parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (append)
        {
            await File.AppendAllTextAsync(fullPath, content, utf8NoBom, context.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(fullPath, content, utf8NoBom, context.CancellationToken).ConfigureAwait(false);
        }

        var info = new FileInfo(fullPath);
        var actionLabel = append ? "已追加写入" : "已写入";
        return ToolExecutionResult.Ok($"{actionLabel}文件: {fullPath} (当前大小: {info.Length} 字节)");
    }
}
