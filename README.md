# CAgent

**0 Tokens · 0 门槛 · 低内存跨平台桌面端** | 基于 Microsoft Copilot **个人版**与**工作版 (M365)** 网页端打造的电脑操控 Agent。

---

## 核心特性 (v0.2 持续自治增强版)

1. **极简顶栏 + 一键「✨ 新建对话」自动初始化**
   - 顶栏精简为一行：左侧 **`[个人版]` / `[工作版 M365]`** 秒切与多账号下拉框，右侧仅保留连接状态与 **「✨ 新建对话」** 主按钮（低频设置全部收纳进 **「⚙️ 设置」**）。
   - 点击 **「✨ 新建对话」** 自动完成：启动/连接浏览器 → 点击网页 New Chat 开启全新对话 → 等待登录（如需） → 自动上传 `cagent_system_prompt.txt` 并发送系统协议 → 自动调用 `list_dir` 扫描当前工作目录，无需用户手动复制粘贴任何提示词。
   - 若直接输入任务点击发送，首轮也会自动附带系统协议完成无缝初始化。

2. **类 MCP 持续自治执行 + 每轮自动附加防遗忘消息 (`[CAgent自动消息]`)**
   - 默认开启 **⚡ 执行模式 + 自动连续调用工具**：Copilot 会像挂载了本地 MCP 一样多轮连续干活（分析 → 分段写代码 → 运行验证 → 主动截图自检 → 修复问题），直至达到验收标准，不再每步停下询问。
   - **每条消息自动附加 `[CAgent自动消息]`**：每轮用户消息与工具回传自动附带紧凑协议与工作目录锚点（界面气泡保持干净不显示噪音），彻底解决网页版 Copilot 多轮对话后对系统提示词稀释、降权或遗忘（声称“无法直接调用工具”）的问题。
   - **长文件防截断（分段追加 + 局部替换）**：内置 `write_file` (`append: true` 分段追加) 与 `edit_file` (`oldText` → `newText` 局部精确替换)，并对 `PLACEHOLDER` 占位符自动触发告警重试，彻底杜绝大文件被截断或留空壳。

3. **📋 规划模式 / ⚡ 执行模式 双模态切换**
   - 底部输入栏内置 **`[📋 规划模式]` / `[⚡ 执行模式]`** 一键切换并自动记忆至 `config.json`。
   - **规划模式**下仅放行只读工具（`list_dir`、`read_file`、`screenshot`、`attach_file`），底层硬性拦截任何写文件、改代码、跑终端命令或键鼠操控请求，防止正在讨论方案时 AI 突然动手实操。

4. **主动视觉截图自检（带鼠标坐标高亮）+ 多模态附件 + 桌面键鼠自动化**
   - **`screenshot`**：截屏自动叠加 **20×20 半透明黄色鼠标位置框 + 红色中心十字准星**，向 Copilot 同时回传物理/逻辑分辨率与鼠标 `(X, Y)` 坐标，并在系统协议中引导 AI 生成网页/GUI 后主动打开并截图自检排版与交互。
   - **`attach_file`**：主动将本地任意图片、日志或文档上传至 Copilot 多模态输入槽。
   - **`ui_automation`**：支持 `list_windows`、`focus_window`、`topmost_window`、`mouse_move`、`mouse_click`、`key_press`（内置拦截 `Alt+F4` 等危险热键护栏）。

5. **工作目录强约束 + 四层抗网页改版引擎**
   - 修复 Windows 下无盘符路径（如 `/app.html`）、资源管理器带引号路径及 `.NET` `[System.IO.File]` 工作目录漂移问题，确保生成代码 100% 落在用户指定的 **本地工作目录**。
   - 保留单进程常驻 CDP + 网络流旁路监听（WebSocket / SignalR / SSE 零轮询）+ 穿透式 DOM 兜底（自动还原代码块与未闭合围栏等待）+ 外置 `adapters.json` + **3 秒点击自愈校准**。

---

## 快速运行与测试

环境要求：[`.NET 9 SDK`](https://dotnet.microsoft.com/download/dotnet/9.0) + 系统已安装 Microsoft Edge 或 Google Chrome。

```bash
# 运行全部自动化测试（单元测试 + Mock CDP 集成测试 + Avalonia Headless UI 渲染测试）
dotnet test

# 启动桌面客户端
dotnet run --project src/CAgent.Desktop
```

---

## 跨平台独立单文件打包（目标电脑免装 .NET 环境）

```bash
# Windows x64 单文件打包
dotnet publish src/CAgent.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64

# Linux x64 单文件打包
dotnet publish src/CAgent.Desktop -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish/linux-x64

# macOS Apple Silicon (M1/M2/M3/M4) 单文件打包
dotnet publish src/CAgent.Desktop -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o publish/osx-arm64
```

---

## 免责声明

- 本项目通过本地浏览器调试协议（CDP）辅助自动化用户自身操作，不涉及任何第三方中转、逆向签名或网页反代。
- 请务必在可信工作目录中使用，讨论方案时可切换至 **📋 规划模式**，合理设置防风控延迟，遵守 Microsoft Copilot 相关服务条款。
