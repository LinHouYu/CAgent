# CAgent

**0 Tokens · 0 门槛 · 低内存跨平台桌面端** | 基于 Microsoft Copilot **个人版**与**工作版 (M365)** 网页端打造的电脑操控 Agent。

---

## 核心特性 (MVP v0.1)

1. **顶部一键双版本与多账号切换**
   - 顶部导航栏内置 **`[个人版 Copilot]` / `[工作版 M365]`** 分段秒切开关。
   - 支持 **`+ 添加账号`** 创建任意多个独立浏览器会话槽位（独立持久化 Profile 目录 + 独立本地调试端口），个人 MSA 账号与公司 Entra ID 企业号可同时保持登录，互不串 Cookie。
2. **方案 A：单进程常驻 CDP + 四层抗网页改版引擎**
   - **第 1 层（网络流被动旁路监听）**：直接监听浏览器收到的 Copilot WebSocket / M365 SignalR (`\u001e`) / SSE 原始网络帧，**0 轮询、0 延迟**提取纯净 Markdown，网页 CSS 类名或气泡 DOM 改版不影响读取；若流协议变更则自动降级至 DOM 增量监控。
   - **第 2 层（穿透式语义寻址 + 协议级输入）**：自动穿透多级 `iframe`（含跨域 OOPIF）与 `Shadow DOM` 评分定位输入框，使用 CDP `Input.insertText` 原生注入，通吃 `<textarea>` 与 Fluent UI 富文本编辑器。
   - **第 3 层（外置 `adapters.json` 热更新配置）**：URL 匹配与候选选择器外置存储，无需重编译即可热调整。
   - **第 4 层（3 秒点击自愈校准）**：点击顶部 **`🎯 校准输入框`** 并在网页输入框左键点一下，自动提取特征并永久保存。
3. **跨平台核心工具集与长度保护**
   - 内置 `list_dir`、`read_file`、`write_file`、`run_shell`（Windows 下自动强制 PowerShell UTF-8 防中文乱码，macOS/Linux 下自动调用 `bash`）、`screenshot`（屏幕截图自动注入 Copilot 多模态视觉）。
   - 内置 **Copilot 输入字数自适应截断器**：超长输出自动保留首尾并落盘完整日志至 `.cagent/tool_outputs/`，防止撑爆网页输入框。
4. **安全护栏与 UI 配置全量记忆 (`config.json`)**
   - 默认开启**人工审批模式**，支持切换**自动连续执行（带最大步数防死循环熔断）**、**一键急停（强杀子进程树）**与**拟人化随机延迟防风控**。
   - 窗口尺寸坐标、深浅色主题、上次选中的版本/账号、工作目录及所有安全参数自动防抖保存至系统用户配置目录 `CAgent/config.json`，重启软件自动还原。

---

## 快速运行与测试

环境要求：[`.NET 9 SDK`](https://dotnet.microsoft.com/download/dotnet/9.0) + 系统已安装 Microsoft Edge 或 Google Chrome。

```bash
# 运行全部自动化测试（含单元测试、Mock CDP 集成测试与 Avalonia Headless UI 测试）
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
- 请务必在可信工作目录中使用并审查 AI 请求执行的终端命令，合理设置防风控延迟，遵守 Microsoft Copilot 相关服务条款。
