using System.Buffers.Binary;
using System.Text;
using CAgent.Core.Abstractions;
using CAgent.Core.Configuration;
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

    [Fact]
    public void ToolCallParser_HandlesFenceStrippedDomJson_AndAppendsAutoReinforcementFooter()
    {
        // Simulate Copilot DOM innerText where markdown backticks were stripped by webpage rendering
        var domStrippedReply = """
        好的，我先读取文件确认当前内容（普通对象 {"foo":"bar"} 不应触发）：
        cagent
        {"tool":"read_file","args":{"path":"src/main.js","startLine":1,"endLine":80}}
        """;

        var calls = ToolCallParser.ParseToolCalls(domStrippedReply);
        var call = Assert.Single(calls);
        Assert.Equal("read_file", call.ToolName);
        Assert.Equal("src/main.js", call.GetString("path"));

        // Verify per-turn [CAgent自动消息] footer in Execute and Plan modes + idempotency
        var execMsg = ToolCallParser.AppendAutoReinforcementFooter("做一个计算器网页", _workspaceDir, AgentInteractionMode.Execute);
        Assert.Contains("[CAgent自动消息]", execMsg);
        Assert.Contains(_workspaceDir, execMsg);
        Assert.Contains("执行模式", execMsg);
        Assert.Contains("screenshot", execMsg);
        Assert.Equal(execMsg, ToolCallParser.AppendAutoReinforcementFooter(execMsg, _workspaceDir, AgentInteractionMode.Execute));

        var planMsg = ToolCallParser.AppendAutoReinforcementFooter("先讨论架构", _workspaceDir, AgentInteractionMode.Plan);
        Assert.Contains("规划模式", planMsg);
        Assert.Contains("严禁调用", planMsg);
    }

    [Fact]
    public void PathResolver_FixesWindowsRootlessSlashBug_AndKeepsFilesStrictlyInWorkspace()
    {
        var quotedWs = $"  \"{_workspaceDir}\"  ";

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                Path.Combine(_workspaceDir, "translator_app.html"),
                PathResolver.ResolveWorkspacePath(quotedWs, "/translator_app.html"));

            Assert.Equal(
                Path.Combine(_workspaceDir, "sub", "app.js"),
                PathResolver.ResolveWorkspacePath(quotedWs, @"\sub\app.js"));
        }

        Assert.Equal(
            Path.Combine(_workspaceDir, "index.html"),
            PathResolver.ResolveWorkspacePath(quotedWs, "/workspace/index.html"));

        Assert.Equal(
            Path.Combine(_workspaceDir, "script.ps1"),
            PathResolver.ResolveWorkspacePath(quotedWs, "$PWD/script.ps1"));

        Assert.Equal(
            Path.Combine(_workspaceDir, "clean.txt"),
            PathResolver.ResolveWorkspacePath(quotedWs, "\"clean.txt\""));
    }

    [Fact]
    public async Task WriteFileChunkedAppend_PlaceholderWarning_EditFileTool_AndAttachFileTool_WorkEndToEnd()
    {
        var registry = BuiltInTools.CreateDefaultRegistry();
        var ctx = new ToolExecutionContext(_workspaceDir, TimeSpan.FromSeconds(15));

        // 1. Placeholder anti-truncation warning check
        var placeholderReq = ToolCallParser.ParseToolCalls("""
        ```cagent
        {"tool":"write_file","args":{"path":"app.html","content":"PLACEHOLDER"}}
        ```
        """).Single();
        var placeholderRes = await registry.ExecuteAsync(placeholderReq, ctx);
        Assert.True(placeholderRes.IsSuccess);
        Assert.Contains("PLACEHOLDER 占位符", placeholderRes.Output);

        // 2. Multi-turn chunked write_file (append: false -> append: true)
        var chunk1 = ToolCallParser.ParseToolCalls("""
        ```cagent
        {"tool":"write_file","args":{"path":"app.html","content":"<html>\r\n<body>\r\n<h1>Old Title</h1>\r\n","append":false}}
        ```
        """).Single();
        var chunk2 = ToolCallParser.ParseToolCalls("""
        ```cagent
        {"tool":"write_file","args":{"path":"app.html","content":"<p>Chunk 2 Ready</p>\r\n</body>\r\n</html>","append":true}}
        ```
        """).Single();
        Assert.True((await registry.ExecuteAsync(chunk1, ctx)).IsSuccess);
        var res2 = await registry.ExecuteAsync(chunk2, ctx);
        Assert.True(res2.IsSuccess);
        Assert.Contains("已追加写入", res2.Output);

        // 3. EditFileTool partial replacement (LF oldText matching CRLF file seamlessly)
        var editReq = ToolCallParser.ParseToolCalls("""
        ```cagent
        {"tool":"edit_file","args":{"path":"app.html","oldText":"<body>\n<h1>Old Title</h1>","newText":"<body>\n<h1>Updated CAgent v0.2</h1>"}}
        ```
        """).Single();
        var editRes = await registry.ExecuteAsync(editReq, ctx);
        Assert.True(editRes.IsSuccess);

        var finalHtml = await File.ReadAllTextAsync(Path.Combine(_workspaceDir, "app.html"), Encoding.UTF8);
        Assert.Contains("<h1>Updated CAgent v0.2</h1>", finalHtml);
        Assert.Contains("<p>Chunk 2 Ready</p>", finalHtml);

        // 4. AttachFileTool multimodal attachment
        var attachReq = ToolCallParser.ParseToolCalls("""
        ```cagent
        {"tool":"attach_file","args":{"path":"app.html"}}
        ```
        """).Single();
        var attachRes = await registry.ExecuteAsync(attachReq, ctx);
        Assert.True(attachRes.IsSuccess);
        Assert.Equal(Path.Combine(_workspaceDir, "app.html"), Assert.Single(attachRes.AttachmentPaths!));
    }

    [Fact]
    public async Task RunShellTool_SyncsProcessAndDotNetCurrentDirectoryToWorkspace()
    {
        var tool = new RunShellTool();
        var ctx = new ToolExecutionContext(_workspaceDir, TimeSpan.FromSeconds(20));

        // Specifically test [System.IO.File]::WriteAllText with a bare relative filename on Windows PowerShell!
        var cmd = OperatingSystem.IsWindows()
            ? "[System.IO.File]::WriteAllText('dotnet_cwd_verify.txt', 'written_in_workspace')"
            : "printf 'written_in_workspace' > dotnet_cwd_verify.txt";

        var req = ToolCallParser.ParseToolCalls($$$"""
        ```cagent
        {"tool":"run_shell","args":{"command":"{{{cmd}}}"}}
        ```
        """).Single();

        var res = await tool.ExecuteAsync(req, ctx);
        Assert.True(res.IsSuccess);

        var expectedFile = Path.Combine(_workspaceDir, "dotnet_cwd_verify.txt");
        Assert.True(File.Exists(expectedFile));
        Assert.Equal("written_in_workspace", await File.ReadAllTextAsync(expectedFile));
    }

    [Fact]
    public async Task PngCursorHighlight_ScreenshotMetadata_AndUiAutomationTool_WorkAndBlockDangerousKeys()
    {
        // 1. Verify yellow 20x20 cursor highlight + red center crosshair on BGRA buffer
        var pixels = new byte[30 * 30 * 4];
        PngEncoder.DrawCursorHighlight(pixels, 30, 30, 15, 15, halfSize: 10);
        var centerIdx = (15 * 30 + 15) * 4;
        Assert.Equal(30, pixels[centerIdx]);      // B (red center crosshair)
        Assert.Equal(30, pixels[centerIdx + 1]);  // G
        Assert.Equal(245, pixels[centerIdx + 2]); // R

        var boxInteriorIdx = (10 * 30 + 10) * 4;
        Assert.True(pixels[boxInteriorIdx + 1] > 100 && pixels[boxInteriorIdx + 2] > 100); // Yellow blend (G & R > 100)

        // 2. Verify ScreenshotTool reports physical/logical dimensions & cursor coordinates
        var metaBackend = new MetadataScreenCaptureBackend();
        var shotTool = new ScreenshotTool(metaBackend);
        var shotReq = ToolCallParser.ParseToolCalls("```cagent\n{\"tool\":\"screenshot\",\"args\":{}}\n```").Single();
        var shotRes = await shotTool.ExecuteAsync(shotReq, new ToolExecutionContext(_workspaceDir, TimeSpan.FromSeconds(5)));
        Assert.True(shotRes.IsSuccess);
        Assert.Contains("1920x1080", shotRes.Output);
        Assert.Contains("X=640, Y=360", shotRes.Output);

        // 3. Verify UiAutomationTool actions & ALT+F4 safety block
        var uiBackend = new FakeUiAutomationBackend();
        var uiTool = new UiAutomationTool(uiBackend);
        var ctx = new ToolExecutionContext(_workspaceDir, TimeSpan.FromSeconds(5));

        var listWins = await uiTool.ExecuteAsync(
            ToolCallParser.ParseToolCalls("```cagent\n{\"tool\":\"ui_automation\",\"args\":{\"action\":\"list_windows\"}}\n```").Single(),
            ctx);
        Assert.True(listWins.IsSuccess);
        Assert.Contains("Translator Demo", listWins.Output);

        var clickRes = await uiTool.ExecuteAsync(
            ToolCallParser.ParseToolCalls("```cagent\n{\"tool\":\"ui_automation\",\"args\":{\"action\":\"mouse_click\",\"x\":500,\"y\":320}}\n```").Single(),
            ctx);
        Assert.True(clickRes.IsSuccess);
        Assert.Equal((500, 320), uiBackend.LastClick);

        var dangerousKeys = await uiTool.ExecuteAsync(
            ToolCallParser.ParseToolCalls("```cagent\n{\"tool\":\"ui_automation\",\"args\":{\"action\":\"key_press\",\"keys\":\"%{F4}\"}}\n```").Single(),
            ctx);
        Assert.False(dangerousKeys.IsSuccess);
        Assert.Contains("安全护栏", dangerousKeys.Output);
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

    private sealed class MetadataScreenCaptureBackend : IScreenCaptureBackend
    {
        public Task<(int Width, int Height)> CapturePngAsync(string outputPath, CancellationToken cancellationToken) =>
            Task.FromResult((1920, 1080));

        public Task<ScreenCaptureMetadata> CapturePngWithMetadataAsync(string outputPath, CancellationToken cancellationToken)
        {
            var pixels = new byte[4 * 4 * 4];
            using var fs = File.Create(outputPath);
            PngEncoder.WriteBgra32AsPng(fs, 4, 4, pixels);
            return Task.FromResult(new ScreenCaptureMetadata(1920, 1080, 1536, 864, 640, 360));
        }
    }

    private sealed class FakeUiAutomationBackend : IUiAutomationBackend
    {
        public (int X, int Y) LastClick { get; private set; }

        public IReadOnlyList<WindowSummaryInfo> ListVisibleWindows() =>
            [new WindowSummaryInfo(100200, "Translator Demo - Microsoft Edge", "Chrome_WidgetWin_1", 4321)];

        public bool FocusWindow(long? hwnd, string? titleKeyword, out string detail)
        {
            detail = "已激活窗口 Translator Demo";
            return true;
        }

        public bool SetTopMost(long hwnd, bool topMost, out string detail)
        {
            detail = $"hwnd={hwnd} topMost={topMost}";
            return true;
        }

        public (int X, int Y) MoveMouse(int x, int y) => (x, y);

        public (int X, int Y) ClickMouse(int x, int y, string button, bool doubleClick)
        {
            LastClick = (x, y);
            return (x, y);
        }

        public bool PressKeys(string keysSpec, long? targetHwnd, out string detail)
        {
            if (keysSpec.Contains("%{F4}", StringComparison.OrdinalIgnoreCase))
            {
                detail = "安全护栏已拒绝发送关闭窗口热键 %{F4}";
                return false;
            }
            detail = $"已发送按键 {keysSpec}";
            return true;
        }
    }
}
