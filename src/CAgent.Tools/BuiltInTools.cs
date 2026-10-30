using CAgent.Core.Abstractions;

namespace CAgent.Tools;

public static class BuiltInTools
{
    public static ToolRegistry CreateDefaultRegistry(
        IScreenCaptureBackend? customScreenCapture = null,
        IUiAutomationBackend? customUiAutomation = null) =>
        new(
        [
            new ListDirTool(),
            new ReadFileTool(),
            new WriteFileTool(),
            new EditFileTool(),
            new RunShellTool(),
            new ScreenshotTool(customScreenCapture),
            new AttachFileTool("attach_file"),
            new AttachFileTool("upload_file"),
            new UiAutomationTool(customUiAutomation)
        ]);
}
