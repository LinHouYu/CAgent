using System.Diagnostics;
using System.Text;
using CAgent.Core.Abstractions;
using CAgent.Core.Protocol;

namespace CAgent.Tools;

public sealed class RunShellTool : ITool
{
    public string Name => "run_shell";
    public bool IsMutating => true;

    public async Task<ToolExecutionResult> ExecuteAsync(ToolCallRequest request, ToolExecutionContext context)
    {
        var command = request.GetString("command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return ToolExecutionResult.Fail("缺少必填参数 'command'。");
        }

        var workingDir = ResolveAndEnsureWorkingDirectory(context.WorkspacePath);
        var psi = CreateCrossPlatformProcessStartInfo(command, workingDir);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdout) { stdout.AppendLine(e.Data); }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr) { stderr.AppendLine(e.Data); }
            }
        };

        var sw = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
            {
                return ToolExecutionResult.Fail("无法启动终端子进程。");
            }
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"启动终端失败: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeout = context.CommandTimeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(60)
            : context.CommandTimeout;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken,
            timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            if (context.CancellationToken.IsCancellationRequested)
            {
                throw; // Emergency stop requested by user
            }
            timedOut = true;
        }

        sw.Stop();

        string outText;
        string errText;
        lock (stdout) { outText = stdout.ToString().TrimEnd(); }
        lock (stderr) { errText = stderr.ToString().TrimEnd(); }

        var summary = new StringBuilder();
        summary.AppendLine($"工作目录: {workingDir}");
        if (timedOut)
        {
            summary.AppendLine($"[命令执行超时 ({timeout.TotalSeconds:F0}s)，已强制终止进程树]");
        }
        else
        {
            summary.AppendLine($"ExitCode: {process.ExitCode} | 耗时: {sw.ElapsedMilliseconds} ms");
        }

        if (!string.IsNullOrWhiteSpace(outText))
        {
            summary.AppendLine("--- STDOUT ---");
            summary.AppendLine(outText);
        }

        if (!string.IsNullOrWhiteSpace(errText))
        {
            summary.AppendLine("--- STDERR ---");
            summary.AppendLine(errText);
        }

        if (string.IsNullOrWhiteSpace(outText) && string.IsNullOrWhiteSpace(errText))
        {
            summary.AppendLine("(无控制台输出)");
        }

        var ok = !timedOut && process.ExitCode == 0;
        return new ToolExecutionResult(ok, summary.ToString().TrimEnd(), []);
    }

    private static string ResolveAndEnsureWorkingDirectory(string? workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            var full = Path.GetFullPath(workspacePath);
            if (!Directory.Exists(full))
            {
                Directory.CreateDirectory(full);
            }
            return full;
        }
        return Directory.GetCurrentDirectory();
    }

    private static ProcessStartInfo CreateCrossPlatformProcessStartInfo(string command, string workingDir)
    {
        var utf8NoBom = new UTF8Encoding(false);

        if (OperatingSystem.IsWindows())
        {
            var shellExe = FindExecutableInPath("pwsh.exe") ?? "powershell.exe";
            var psEscapedDir = workingDir.Replace("'", "''");

            // Force UTF-8 I/O AND sync both PowerShell $PWD and .NET [Environment]::CurrentDirectory
            // so [IO.File]::WriteAllText('foo.html', ...) always lands inside WorkspacePath!
            var wrappedScript =
                "$Utf8NoBom = [System.Text.UTF8Encoding]::new($false); " +
                "[Console]::InputEncoding = $Utf8NoBom; " +
                "[Console]::OutputEncoding = $Utf8NoBom; " +
                "$OutputEncoding = $Utf8NoBom; " +
                "$ProgressPreference = 'SilentlyContinue'; " +
                $"Set-Location -LiteralPath '{psEscapedDir}'; " +
                "[System.Environment]::CurrentDirectory = (Get-Location -PSProvider FileSystem).ProviderPath; " +
                command;

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrappedScript));
            return new ProcessStartInfo
            {
                FileName = shellExe,
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = utf8NoBom,
                StandardErrorEncoding = utf8NoBom
            };
        }
        else
        {
            var shellExe = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
            var posixEscapedDir = workingDir.Replace("'", "'\"'\"'");
            var wrappedPosix = $"cd '{posixEscapedDir}' && {command}";

            var psi = new ProcessStartInfo
            {
                FileName = shellExe,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = utf8NoBom,
                StandardErrorEncoding = utf8NoBom
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(wrappedPosix);
            return psi;
        }
    }

    private static string? FindExecutableInPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore invalid PATH entries
            }
        }
        return null;
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore already-exited race
        }
    }
}
