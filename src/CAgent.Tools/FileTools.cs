using System.Text;
using CAgent.Core.Abstractions;
using CAgent.Core.Protocol;

namespace CAgent.Tools;

public static class PathResolver
{
    public static string ResolveWorkspacePath(string workspacePath, string inputPath)
    {
        var cleanWs = workspacePath?.Trim().Trim('"', '\'');
        var baseDir = string.IsNullOrWhiteSpace(cleanWs)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(cleanWs);

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return baseDir;
        }

        var raw = inputPath.Trim().Trim('"', '\'').Trim();
        if (string.IsNullOrEmpty(raw) || raw is "." or "./" or ".\\")
        {
            return baseDir;
        }

        // Strip common shell current-dir prefixes that LLMs sometimes emit
        string[] pwdPrefixes = ["$PWD/", "$PWD\\", "${PWD}/", "${PWD}\\", "%CD%\\", "%CD%/"];
        foreach (var prefix in pwdPrefixes)
        {
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                raw = raw[prefix.Length..];
                break;
            }
        }
        if (raw.Equals("$PWD", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("${PWD}", StringComparison.OrdinalIgnoreCase))
        {
            return baseDir;
        }

        // Expand ~/ to user home directory
        if (raw == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        if (raw.StartsWith("~/", StringComparison.Ordinal) || raw.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var sub = raw[2..].Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(home, sub));
        }

        var normalized = raw
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (OperatingSystem.IsWindows())
        {
            // On Windows, "/app.html" or "\src\app.js" is NOT fully qualified (it lacks drive letter "C:\" or UNC "\\").
            // Treat rootless leading-slash paths as workspace-relative instead of dumping files into C:\!
            if (Path.IsPathFullyQualified(normalized))
            {
                return Path.GetFullPath(normalized);
            }

            var rel = normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (rel.StartsWith($"workspace{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetFileName(baseDir.TrimEnd(Path.DirectorySeparatorChar)), "workspace", StringComparison.OrdinalIgnoreCase))
            {
                rel = rel[("workspace".Length + 1)..];
            }

            return string.IsNullOrWhiteSpace(rel)
                ? baseDir
                : Path.GetFullPath(Path.Combine(baseDir, rel));
        }

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
                    return ToolExecutionResult.Fail(
                        $"文件 '{fullPath}' 疑似二进制/多媒体文件 ({fs.Length} 字节)，不支持按纯文本读取；若是图片/PDF/Office文档，请改用 attach_file 工具上传识别。");
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
        var msg = $"{actionLabel}文件: {fullPath} (本次写入 {content.Length} 字符，当前文件总大小: {info.Length} 字节)";

        var trimmed = content.Trim();
        if (trimmed.Equals("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) ||
            (trimmed.Length < 60 && trimmed.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)))
        {
            msg += "\n⚠️ 检测到刚写入的是 PLACEHOLDER 占位符！严禁停下或只留空壳，请立即在下一轮调用 write_file (先用 append:false 写入第1段真实代码，后续多轮用 append:true 分段追加) 直至完整实现全部功能！";
        }

        return ToolExecutionResult.Ok(msg);
    }
}

public sealed class EditFileTool : ITool
{
    public string Name => "edit_file";
    public bool IsMutating => true;

    public async Task<ToolExecutionResult> ExecuteAsync(ToolCallRequest request, ToolExecutionContext context)
    {
        var rawPath = request.GetString("path");
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return ToolExecutionResult.Fail("缺少必填参数 'path'。");
        }

        var oldText = request.GetString("oldText");
        if (string.IsNullOrEmpty(oldText))
        {
            oldText = request.GetString("target");
        }
        if (string.IsNullOrEmpty(oldText))
        {
            return ToolExecutionResult.Fail("缺少必填参数 'oldText'（要被替换的原始代码片段）。");
        }

        var newText = request.Arguments.TryGetProperty("newText", out _)
            ? request.GetString("newText")
            : request.GetString("replacement");
        var replaceAll = request.GetBool("replaceAll", defaultValue: false);

        var fullPath = PathResolver.ResolveWorkspacePath(context.WorkspacePath, rawPath);
        if (!File.Exists(fullPath))
        {
            return ToolExecutionResult.Fail($"目标文件不存在: {fullPath}");
        }

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var original = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, context.CancellationToken).ConfigureAwait(false);

        string updated;
        if (original.Contains(oldText, StringComparison.Ordinal))
        {
            updated = replaceAll
                ? original.Replace(oldText, newText, StringComparison.Ordinal)
                : ReplaceFirst(original, oldText, newText);
        }
        else
        {
            // Tolerate CRLF vs LF differences seamlessly across Windows/Unix
            var usesCrLf = original.Contains("\r\n", StringComparison.Ordinal);
            var normalizedOriginal = original.Replace("\r\n", "\n");
            var normalizedOld = oldText.Replace("\r\n", "\n");
            var normalizedNew = newText.Replace("\r\n", "\n");

            if (!normalizedOriginal.Contains(normalizedOld, StringComparison.Ordinal))
            {
                return ToolExecutionResult.Fail(
                    $"在文件 '{fullPath}' 中未找到指定的 oldText 片段。请先用 read_file 确认最新行内容后再重试。");
            }

            var replacedLf = replaceAll
                ? normalizedOriginal.Replace(normalizedOld, normalizedNew, StringComparison.Ordinal)
                : ReplaceFirst(normalizedOriginal, normalizedOld, normalizedNew);

            updated = usesCrLf ? replacedLf.Replace("\n", "\r\n") : replacedLf;
        }

        await File.WriteAllTextAsync(fullPath, updated, utf8NoBom, context.CancellationToken).ConfigureAwait(false);
        var info = new FileInfo(fullPath);
        return ToolExecutionResult.Ok($"已局部更新文件: {fullPath} (当前总大小: {info.Length} 字节)");
    }

    private static string ReplaceFirst(string source, string search, string replacement)
    {
        var idx = source.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0)
        {
            return source;
        }
        return string.Concat(source.AsSpan(0, idx), replacement, source.AsSpan(idx + search.Length));
    }
}

public sealed class AttachFileTool(string toolName = "attach_file") : ITool
{
    private const long MaxSingleFileSizeBytes = 50L * 1024 * 1024; // 50 MB

    public string Name => toolName;
    public bool IsMutating => false;

    public Task<ToolExecutionResult> ExecuteAsync(ToolCallRequest request, ToolExecutionContext context)
    {
        var rawPaths = new List<string>(request.GetStringArray("paths"));
        var single = request.GetString("path");
        if (!string.IsNullOrWhiteSpace(single) && !rawPaths.Contains(single, StringComparer.OrdinalIgnoreCase))
        {
            rawPaths.Insert(0, single);
        }

        if (rawPaths.Count == 0)
        {
            return Task.FromResult(ToolExecutionResult.Fail("请提供 'path' 或 'paths' 参数指定要上传的本地文件路径。"));
        }

        if (rawPaths.Count > 10)
        {
            return Task.FromResult(ToolExecutionResult.Fail("单次最多同时附带 10 个文件，请分批上传。"));
        }

        var resolved = new List<string>();
        foreach (var raw in rawPaths)
        {
            var full = PathResolver.ResolveWorkspacePath(context.WorkspacePath, raw);
            if (!File.Exists(full))
            {
                return Task.FromResult(ToolExecutionResult.Fail($"要上传的文件不存在: {full}"));
            }
            var info = new FileInfo(full);
            if (info.Length > MaxSingleFileSizeBytes)
            {
                return Task.FromResult(ToolExecutionResult.Fail($"文件 '{info.Name}' 大小超过 50MB 限制 ({info.Length} 字节)。"));
            }
            resolved.Add(full);
        }

        var summaryNames = string.Join(", ", resolved.Select(Path.GetFileName));
        return Task.FromResult(ToolExecutionResult.Ok(
            $"已将 {resolved.Count} 个本地文件随本消息上传至对话供你多模态查看: {summaryNames}",
            resolved));
    }
}
