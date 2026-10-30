using System.Buffers.Binary;
using System.Text;
using CAgent.Core.Abstractions;
using CAgent.Core.Protocol;
using CAgent.Tools;

namespace CAgent.Tests;

public sealed class ProtocolAndToolsTests : IDisposable
{
    private readonly string _workspaceDir;

    public ProtocolAndToolsTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), $"cagent_tools_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceDir);
    }

    [Fact]
    public void ToolCallParser_ExtractsCagentBlocks_ToleratesTrailingCommas_AndIgnoresNormalJson()
    {
        var reply = """
        我先来看一下项目结构，下面是一个普通示例 JSON（不应触发工具）：
        ```json
        {"name": "demo-package", "version": "1.0.0"}
        ```

        现在正式调用工具扫描目录：
        ```cagent
        {
          // 读取根目录
          "tool": "list_dir",
          "args": {
            "path": ".",
            "maxDepth": 2,
          },
        }
        ```
        """;

        var calls = ToolCallParser.ParseToolCalls(reply);
        var call = Assert.Single(calls);
        Assert.Equal("list_dir", call.ToolName);
        Assert.Equal(".", call.GetString("path"));
        Assert.Equal(2, call.GetInt("maxDepth", 1));
    }

    [Fact]
    public void OutputTruncator_RespectsMaxCharsAndPersistsFullLog()
    {
        var shortResult = OutputTruncator.TruncateIfNeeded("hello world", 1000, _workspaceDir);
        Assert.False(shortResult.WasTruncated);
        Assert.Null(shortResult.FullOutputLogPath);

        var longText = "HEAD_MARKER_" + new string('A', 4500) + "_TAIL_MARKER";
        var truncated = OutputTruncator.TruncateIfNeeded(longText, 800, _workspaceDir);

        Assert.True(truncated.WasTruncated);
        Assert.True(truncated.Content.Length <= 800);
        Assert.StartsWith("HEAD_MARKER_", truncated.Content);
        Assert.EndsWith("_TAIL_MARKER", truncated.Content);
        Assert.NotNull(truncated.FullOutputLogPath);
        Assert.True(File.Exists(truncated.FullOutputLogPath!));
        Assert.Equal(longText, File.ReadAllText(truncated.FullOutputLogPath!, Encoding.UTF8));
    }

    [Fact]
    public async Task FileTools_WriteReadPaginateDetectBinaryAndListCleanly()
    {
        var registry = BuiltInTools.CreateDefaultRegistry();
        var ctx = new ToolExecutionContext(_workspaceDir, TimeSpan.FromSeconds(15));

        // Create ignored directories to ensure list_dir skips them
        Directory.CreateDirectory(Path.Combine(_workspaceDir, ".git"));
        File.WriteAllText(Path.Combine(_workspaceDir, ".git", "HEAD"), "ref: refs/heads/main");

        // 1. WriteFileTool
        var writeReq = ToolCallParser.ParseToolCalls("""
        ```cagent
        {"tool":"write_file","args":{"path":"docs/notes.txt","content":"第一行中文\n第二行内容\n第三行结尾"}}
        ```
        """).Single();
        var writeRes = await registry.ExecuteAsync(writeReq, ctx);
        Assert.True(writeRes.IsSuccess);

        // 2. ReadFileTool with startLine/endLine pagination
        var readReq = ToolCallParser.ParseToolCalls("""
        ```cagent
        {"tool":"read_file","args":{"path":"docs/notes.txt","startLine":2,"endLine":3}}
        ```
        """).Single();
        var readRes = await registry.ExecuteAsync(readReq, ctx);
        Assert.True(readRes.IsSuccess);
        Assert.Contains("第二行内容", readRes.Output);
        Assert.Contains("第三行结尾", readRes.Output);
        Assert.DoesNotContain("第一行中文", readRes.Output);

        // 3. Binary file rejection
        var binPath = Path.Combine(_workspaceDir, "sample.bin");
        await File.WriteAllBytesAsync(binPath, [0x4D, 0x5A, 0x00, 0x90]);
        var binReq = ToolCallParser.ParseToolCalls("""
        ```cagent
        {"tool":"read_file","args":{"path":"sample.bin"}}
        ```
        """).Single();
        var binRes = await registry.ExecuteAsync(binReq, ctx);
        Assert.False(binRes.IsSuccess);
        Assert.Contains("二进制", binRes.Output);

        // 4. ListDirTool
        var listReq = ToolCallParser.ParseToolCalls("""
        ```cagent
        {"tool":"list_dir","args":{"path":".","maxDepth":3}}
        ```
        """).Single();
        var listRes = await registry.ExecuteAsync(listReq, ctx);
        Assert.True(listRes.IsSuccess);
        Assert.Contains("notes.txt", listRes.Output);
        Assert.DoesNotContain("HEAD", listRes.Output); // .git ignored
    }

    [Fact]
    public async Task RunShellTool_PreservesUtf8ChineseAndEnforcesTimeout()
    {
        var tool = new RunShellTool();
        var ctx = new ToolExecutionContext(_workspaceDir, TimeSpan.FromSeconds(20));

        var echoCmd = OperatingSystem.IsWindows()
            ? "Write-Output '你好 CAgent 跨平台测试 🚀'"
            : "printf '你好 CAgent 跨平台测试 🚀\n'";

        var req = ToolCallParser.ParseToolCalls($$$"""
        ```cagent
        {"tool":"run_shell","args":{"command":"{{{echoCmd}}}"}}
        ```
        """).Single();

        var res = await tool.ExecuteAsync(req, ctx);
        Assert.True(res.IsSuccess);
        Assert.Contains("你好 CAgent 跨平台测试 🚀", res.Output);

        // Verify timeout kills hung command cleanly
        var sleepCmd = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 15" : "sleep 15";
        var sleepReq = ToolCallParser.ParseToolCalls($$$"""
        ```cagent
        {"tool":"run_shell","args":{"command":"{{{sleepCmd}}}"}}
        ```
        """).Single();

        var shortTimeoutCtx = new ToolExecutionContext(_workspaceDir, TimeSpan.FromMilliseconds(600));
        var timeoutRes = await tool.ExecuteAsync(sleepReq, shortTimeoutCtx);
        Assert.False(timeoutRes.IsSuccess);
        Assert.Contains("超时", timeoutRes.Output);
    }

    [Fact]
    public async Task ScreenshotTool_AndPngEncoder_ProduceValidPngAttachment()
    {
        var fakeBackend = new SyntheticScreenCaptureBackend(4, 3);
        var tool = new ScreenshotTool(fakeBackend);
        var req = ToolCallParser.ParseToolCalls("""
        ```cagent
        {"tool":"screenshot","args":{}}
        ```
        """).Single();

        var res = await tool.ExecuteAsync(req, new ToolExecutionContext(_workspaceDir, TimeSpan.FromSeconds(5)));
        Assert.True(res.IsSuccess);
        var savedPath = Assert.Single(res.AttachmentPaths!);
        Assert.True(File.Exists(savedPath));

        var bytes = await File.ReadAllBytesAsync(savedPath);
        // Verify PNG magic signature: 89 50 4E 47 0D 0A 1A 0A
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        // Verify IHDR width=4, height=3
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspaceDir))
            {
                Directory.Delete(_workspaceDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup locks
        }
    }

    private sealed class SyntheticScreenCaptureBackend(int width, int height) : IScreenCaptureBackend
    {
        public Task<(int Width, int Height)> CapturePngAsync(string outputPath, CancellationToken cancellationToken)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;     // B
                pixels[i + 1] = 128; // G
                pixels[i + 2] = 64;  // R
                pixels[i + 3] = 255; // A
            }
            using var fs = File.Create(outputPath);
            PngEncoder.WriteBgra32AsPng(fs, width, height, pixels);
            return Task.FromResult((width, height));
        }
    }
}
