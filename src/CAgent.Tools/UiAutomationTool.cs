using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CAgent.Core.Abstractions;
using CAgent.Core.Protocol;

namespace CAgent.Tools;

public sealed record WindowSummaryInfo(long Hwnd, string Title, string ClassName, int ProcessId);

public interface IUiAutomationBackend
{
    IReadOnlyList<WindowSummaryInfo> ListVisibleWindows();
    bool FocusWindow(long? hwnd, string? titleKeyword, out string detail);
    bool SetTopMost(long hwnd, bool topMost, out string detail);
    (int X, int Y) MoveMouse(int x, int y);
    (int X, int Y) ClickMouse(int x, int y, string button, bool doubleClick);
    bool PressKeys(string keysSpec, long? targetHwnd, out string detail);
}

public sealed class UiAutomationTool(IUiAutomationBackend? backend = null) : ITool
{
    private readonly IUiAutomationBackend _backend = backend ?? new DefaultUiAutomationBackend();

    public string Name => "ui_automation";
    public bool IsMutating => true;

    public Task<ToolExecutionResult> ExecuteAsync(ToolCallRequest request, ToolExecutionContext context)
    {
        var action = request.GetString("action").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(action))
        {
            return Task.FromResult(ToolExecutionResult.Fail(
                "缺少必填参数 'action'。可选值: list_windows, focus_window, topmost_window, mouse_move, mouse_click, key_press"));
        }

        switch (action)
        {
            case "list_windows":
            {
                var wins = _backend.ListVisibleWindows();
                if (wins.Count == 0)
                {
                    return Task.FromResult(ToolExecutionResult.Ok("当前未枚举到可见顶层窗口。"));
                }

                var sb = new StringBuilder();
                sb.AppendLine($"当前可见顶层窗口 (共 {wins.Count} 个):");
                foreach (var w in wins.Take(80))
                {
                    sb.AppendLine($"- HWND={w.Hwnd} | PID={w.ProcessId} | Class={w.ClassName} | Title={w.Title}");
                }
                return Task.FromResult(ToolExecutionResult.Ok(sb.ToString()));
            }

            case "focus_window":
            {
                var hwndVal = request.GetInt("hwnd", 0);
                var title = request.GetString("title");
                var ok = _backend.FocusWindow(hwndVal != 0 ? hwndVal : null, title, out var detail);
                return Task.FromResult(ok ? ToolExecutionResult.Ok(detail) : ToolExecutionResult.Fail(detail));
            }

            case "topmost_window":
            {
                var hwnd = request.GetInt("hwnd", 0);
                if (hwnd == 0)
                {
                    return Task.FromResult(ToolExecutionResult.Fail("topmost_window 需要提供有效的整数参数 'hwnd'（可先用 list_windows 获取）。"));
                }
                var topMost = request.GetBool("topMost", defaultValue: true);
                var ok = _backend.SetTopMost(hwnd, topMost, out var detail);
                return Task.FromResult(ok ? ToolExecutionResult.Ok(detail) : ToolExecutionResult.Fail(detail));
            }

            case "mouse_move":
            {
                var x = request.GetInt("x", int.MinValue);
                var y = request.GetInt("y", int.MinValue);
                if (x == int.MinValue || y == int.MinValue)
                {
                    return Task.FromResult(ToolExecutionResult.Fail("mouse_move 需要提供整数坐标参数 'x' 和 'y'。"));
                }
                var (actualX, actualY) = _backend.MoveMouse(x, y);
                return Task.FromResult(ToolExecutionResult.Ok(
                    $"已移动鼠标。当前鼠标逻辑坐标: X={actualX}, Y={actualY}（建议点击前调用 screenshot 确认黄色标记框落点）。"));
            }

            case "mouse_click":
            {
                var x = request.GetInt("x", int.MinValue);
                var y = request.GetInt("y", int.MinValue);
                if (x == int.MinValue || y == int.MinValue)
                {
                    return Task.FromResult(ToolExecutionResult.Fail("mouse_click 需要提供整数坐标参数 'x' 和 'y'。"));
                }
                var button = request.GetString("button", "left").Trim().ToLowerInvariant();
                var dbl = request.GetBool("doubleClick", defaultValue: false);
                var (actualX, actualY) = _backend.ClickMouse(x, y, button, dbl);
                return Task.FromResult(ToolExecutionResult.Ok(
                    $"已在坐标 (X={actualX}, Y={actualY}) 执行鼠标{(button == "right" ? "右键" : "左键")}{(dbl ? "双击" : "单击")}。"));
            }

            case "key_press":
            {
                var keys = request.GetString("keys");
                if (string.IsNullOrWhiteSpace(keys))
                {
                    return Task.FromResult(ToolExecutionResult.Fail("key_press 需要提供参数 'keys'（如 'ENTER', 'CTRL+S', 'F5'）。"));
                }

                // Safety guardrail: block blind ALT+F4 so AI doesn't close CAgent or the Copilot browser
                var normalizedKeys = keys.Replace(" ", "").ToUpperInvariant();
                if (normalizedKeys.Contains("ALT+F4", StringComparison.Ordinal))
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "安全拦截：禁止发送 ALT+F4 热键，以免误关 CAgent 或调试浏览器。如需关闭指定子进程，请使用 run_shell 按精确 PID/名称关闭。"));
                }

                var hwndVal = request.GetInt("hwnd", 0);
                var ok = _backend.PressKeys(keys, hwndVal != 0 ? hwndVal : null, out var detail);
                return Task.FromResult(ok ? ToolExecutionResult.Ok(detail) : ToolExecutionResult.Fail(detail));
            }

            default:
                return Task.FromResult(ToolExecutionResult.Fail(
                    $"未知 action '{action}'。支持: list_windows, focus_window, topmost_window, mouse_move, mouse_click, key_press"));
        }
    }
}

public sealed partial class DefaultUiAutomationBackend : IUiAutomationBackend
{
    private static readonly HashSet<string> ProtectedSystemWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
        "DV2ControlHost"
    };

    public IReadOnlyList<WindowSummaryInfo> ListVisibleWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return Win32InputNative.EnumerateVisibleWindows();
    }

    public bool FocusWindow(long? hwnd, string? titleKeyword, out string detail)
    {
        if (!OperatingSystem.IsWindows())
        {
            detail = "当前平台暂仅支持通过 run_shell 调用系统命令（如 osascript / wmctrl）聚焦窗口。";
            return false;
        }

        return Win32InputNative.FocusWindow(hwnd, titleKeyword, out detail);
    }

    public bool SetTopMost(long hwnd, bool topMost, out string detail)
    {
        if (!OperatingSystem.IsWindows())
        {
            detail = "当前平台不支持 Win32 TopMost API。";
            return false;
        }

        return Win32InputNative.SetTopMost((nint)hwnd, topMost, ProtectedSystemWindowClasses, out detail);
    }

    public (int X, int Y) MoveMouse(int x, int y)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("当前仅 Windows 内置原生鼠标移动，Linux/macOS 可通过 run_shell 调用 xdotool/cliclick。");
        }
        return Win32InputNative.MoveCursor(x, y);
    }

    public (int X, int Y) ClickMouse(int x, int y, string button, bool doubleClick)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("当前仅 Windows 内置原生鼠标点击，Linux/macOS 可通过 run_shell 调用 xdotool/cliclick。");
        }
        return Win32InputNative.ClickCursor(x, y, button == "right", doubleClick);
    }

    public bool PressKeys(string keysSpec, long? targetHwnd, out string detail)
    {
        if (!OperatingSystem.IsWindows())
        {
            detail = "当前仅 Windows 内置原生模拟按键，Linux/macOS 可通过 run_shell 调用 xdotool/osascript。";
            return false;
        }
        return Win32InputNative.SendKeyChord(keysSpec, targetHwnd, out detail);
    }

    private static partial class Win32InputNative
    {
        private const int SW_RESTORE = 9;
        private static readonly nint HWND_TOPMOST = new(-1);
        private static readonly nint HWND_NOTOPMOST = new(-2);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(nint hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(nint hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(nint hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, nuint dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

        public static List<WindowSummaryInfo> EnumerateVisibleWindows()
        {
            var list = new List<WindowSummaryInfo>();
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                var titleSb = new StringBuilder(256);
                GetWindowText(hWnd, titleSb, titleSb.Capacity);
                var title = titleSb.ToString().Trim();
                if (string.IsNullOrEmpty(title))
                {
                    return true;
                }

                var clsSb = new StringBuilder(128);
                GetClassName(hWnd, clsSb, clsSb.Capacity);
                GetWindowThreadProcessId(hWnd, out var pid);

                list.Add(new WindowSummaryInfo(hWnd, title, clsSb.ToString(), (int)pid));
                return true;
            }, nint.Zero);

            return list;
        }

        public static bool FocusWindow(long? hwnd, string? titleKeyword, out string detail)
        {
            nint target = nint.Zero;
            string matchedTitle = string.Empty;

            if (hwnd.HasValue && hwnd.Value != 0)
            {
                target = (nint)hwnd.Value;
                var sb = new StringBuilder(256);
                GetWindowText(target, sb, sb.Capacity);
                matchedTitle = sb.ToString();
            }
            else if (!string.IsNullOrWhiteSpace(titleKeyword))
            {
                var found = EnumerateVisibleWindows().FirstOrDefault(w =>
                    w.Title.Contains(titleKeyword, StringComparison.OrdinalIgnoreCase));
                if (found is not null)
                {
                    target = (nint)found.Hwnd;
                    matchedTitle = found.Title;
                }
            }

            if (target == nint.Zero)
            {
                detail = "未找到匹配的可见窗口，请先调用 list_windows 确认标题或句柄。";
                return false;
            }

            if (IsIconic(target))
            {
                ShowWindow(target, SW_RESTORE);
            }

            // Tap ALT virtually to unlock Windows foreground lock restriction safely
            keybd_event(0x12, 0, 0, 0);
            keybd_event(0x12, 0, KEYEVENTF_KEYUP, 0);

            BringWindowToTop(target);
            SetForegroundWindow(target);

            detail = $"已激活并置前窗口: HWND={(long)target} ({matchedTitle})";
            return true;
        }

        public static bool SetTopMost(nint target, bool topMost, HashSet<string> blockedClasses, out string detail)
        {
            var clsSb = new StringBuilder(128);
            GetClassName(target, clsSb, clsSb.Capacity);
            var cls = clsSb.ToString();

            if (blockedClasses.Contains(cls))
            {
                detail = $"安全拦截：禁止对系统桌面/任务栏窗口 ({cls}) 调用置顶操作。";
                return false;
            }

            if (IsIconic(target))
            {
                detail = "目标窗口处于最小化状态，置顶无效；请先调用 focus_window 恢复窗口。";
                return false;
            }

            var ok = SetWindowPos(
                target,
                topMost ? HWND_TOPMOST : HWND_NOTOPMOST,
                0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            detail = ok
                ? $"已将窗口 HWND={(long)target} 的置顶状态设为 {topMost}。"
                : $"设置窗口 HWND={(long)target} 置顶状态失败。";
            return ok;
        }

        public static (int X, int Y) MoveCursor(int x, int y)
        {
            SetCursorPos(x, y);
            return GetCursorPos(out var pt) ? (pt.X, pt.Y) : (x, y);
        }

        public static (int X, int Y) ClickCursor(int x, int y, bool rightButton, bool doubleClick)
        {
            SetCursorPos(x, y);
            Thread.Sleep(40);
            var down = rightButton ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
            var up = rightButton ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;

            mouse_event(down, x, y, 0, 0);
            mouse_event(up, x, y, 0, 0);
            if (doubleClick)
            {
                Thread.Sleep(60);
                mouse_event(down, x, y, 0, 0);
                mouse_event(up, x, y, 0, 0);
            }

            return GetCursorPos(out var pt) ? (pt.X, pt.Y) : (x, y);
        }

        public static bool SendKeyChord(string keysSpec, long? targetHwnd, out string detail)
        {
            if (targetHwnd.HasValue && targetHwnd.Value != 0)
            {
                FocusWindow(targetHwnd.Value, null, out _);
                Thread.Sleep(80);
            }

            var parts = keysSpec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var vks = new List<byte>();
            foreach (var p in parts)
            {
                if (!TryMapVirtualKey(p, out var vk))
                {
                    detail = $"不支持的按键名称 '{p}'（支持 CTRL, SHIFT, ALT, ENTER, SPACE, TAB, ESC, F1-F12, A-Z, 0-9 或十进制虚拟键码）。";
                    return false;
                }
                vks.Add(vk);
            }

            if (vks.Count == 0)
            {
                detail = "按键列表为空。";
                return false;
            }

            foreach (var vk in vks)
            {
                keybd_event(vk, 0, 0, 0);
            }
            Thread.Sleep(30);
            for (var i = vks.Count - 1; i >= 0; i--)
            {
                keybd_event(vks[i], 0, KEYEVENTF_KEYUP, 0);
            }

            detail = $"已发送按键组合: {keysSpec}";
            return true;
        }

        private static bool TryMapVirtualKey(string token, out byte vk)
        {
            var upper = token.Trim().ToUpperInvariant();
            if (upper.Length == 1 && upper[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                vk = (byte)upper[0];
                return true;
            }

            if (upper.StartsWith('F') && int.TryParse(upper[1..], out var fNum) && fNum is >= 1 and <= 12)
            {
                vk = (byte)(0x70 + fNum - 1);
                return true;
            }

            if (byte.TryParse(upper, out var rawCode) && rawCode > 0)
            {
                vk = rawCode;
                return true;
            }

            vk = upper switch
            {
                "CTRL" or "CONTROL" => 0x11,
                "SHIFT" => 0x10,
                "ALT" => 0x12,
                "WIN" or "LWIN" => 0x5B,
                "ENTER" or "RETURN" => 0x0D,
                "ESC" or "ESCAPE" => 0x1B,
                "SPACE" => 0x20,
                "TAB" => 0x09,
                "BACKSPACE" or "BACK" => 0x08,
                "DELETE" or "DEL" => 0x2E,
                "HOME" => 0x24,
                "END" => 0x23,
                "PGUP" or "PAGEUP" => 0x21,
                "PGDN" or "PAGEDOWN" => 0x22,
                "LEFT" => 0x25,
                "UP" => 0x26,
                "RIGHT" => 0x27,
                "DOWN" => 0x28,
                _ => (byte)0
            };
            return vk != 0;
        }
    }
}
