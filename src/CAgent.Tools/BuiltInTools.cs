using CAgent.Core.Abstractions;

namespace CAgent.Tools;

public static class BuiltInTools
{
    public static ToolRegistry CreateDefaultRegistry(IScreenCaptureBackend? customScreenCapture = null) =>
        new(
        [
            new ListDirTool(),
            new ReadFileTool(),
            new WriteFileTool(),
            new RunShellTool(),
            new ScreenshotTool(customScreenCapture)
        ]);
}
