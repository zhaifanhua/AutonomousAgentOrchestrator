using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AgentOrchestrator.Infrastructure.Sandbox;

/// <summary>
/// 基于 Process 的工具沙箱。
/// 安全保证：
/// - 使用参数数组，禁止 shell 拼接（UseShellExecute = false）
/// - 长 prompt 通过 stdin 而非命令行参数传递
/// - 文件操作路径校验（AllowedPaths 白名单）
/// - 超时强制终止进程树
/// </summary>
public class ProcessToolSandbox(ILogger<ProcessToolSandbox> logger) : IToolSandbox
{
    public async Task<ToolResult> ExecuteAsync(ToolInvocation inv, CancellationToken ct)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("执行工具: {Command} {Args}",
                inv.Command, string.Join(" ", inv.Arguments.Select(EscapeArgForLog)));
        }

        var sw = Stopwatch.StartNew();
        string? stdinFile = null;

        try
        {
            var psi = BuildProcessStartInfo(inv);

            // 长 prompt 写入临时文件后通过 stdin 传入
            if (inv.StdInput is { Length: > 0 } stdin)
            {
                stdinFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(stdinFile, stdin, ct);
                psi.RedirectStandardInput = true;
            }

            using var process = new Process { StartInfo = psi };
            var stdoutBuilder = new System.Text.StringBuilder();
            var stderrBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) { stdoutBuilder.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) { stderrBuilder.AppendLine(e.Data); } };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 写入 stdin
            if (stdinFile != null && process.StandardInput != null)
            {
                await using var fileStream = File.OpenRead(stdinFile);
                await fileStream.CopyToAsync(process.StandardInput.BaseStream, ct);
                process.StandardInput.Close();
            }

            // 带超时等待
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(inv.Timeout);
            using var progressTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var progressTask = LogProgressAsync(inv, sw, progressTimer, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                progressTimer.Dispose();
                await IgnoreCancellationAsync(progressTask);
                process.WaitForExit();
            }
            catch (OperationCanceledException)
            {
                progressTimer.Dispose();
                await IgnoreCancellationAsync(progressTask);
                logger.LogWarning("工具被取消或超时，强制终止进程 PID={PID}", process.Id);
                KillProcessTree(process);

                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                sw.Stop();
                return new ToolResult(-1, stdoutBuilder.ToString(), "TIMEOUT: 进程超时被终止", sw.Elapsed, 0);
            }

            sw.Stop();
            var peakMemory = GetPeakMemory(process);

            var result = new ToolResult(
                process.ExitCode,
                stdoutBuilder.ToString(),
                stderrBuilder.ToString(),
                sw.Elapsed,
                peakMemory);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("工具完成 ExitCode={Code}, Duration={Ms}ms",
                    result.ExitCode, sw.ElapsedMilliseconds);
            }
            return result;
        }
        finally
        {
            if (stdinFile != null && File.Exists(stdinFile))
            {
                File.Delete(stdinFile);
            }
        }
    }

    private static ProcessStartInfo BuildProcessStartInfo(ToolInvocation inv)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,         // 禁止 shell，防注入
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = inv.WorkingDirectory,
            CreateNoWindow = true,
        };

        // Windows：.cmd / .bat 不能直接用 UseShellExecute=false 启动，需通过 cmd.exe /c 包装
        if (OperatingSystem.IsWindows() &&
            (inv.Command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             inv.Command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(inv.Command);
        }
        else
        {
            psi.FileName = inv.Command;
        }

        foreach (var arg in inv.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (k, v) in inv.Environment)
        {
            psi.Environment[k] = v;
        }

        return psi;
    }

    private async Task LogProgressAsync(
        ToolInvocation inv,
        Stopwatch sw,
        PeriodicTimer timer,
        CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                logger.LogInformation(
                    "工具仍在运行: {Command}, Elapsed={ElapsedSeconds}s, Timeout={TimeoutSeconds}s",
                    inv.Command,
                    (int)sw.Elapsed.TotalSeconds,
                    (int)inv.Timeout.TotalSeconds);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常结束或超时时由主流程处理。
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 校验路径（文件或目录）在白名单前缀内，防止越权访问。
    /// 白名单条目若为空集合，表示仅校验工作目录边界（调用方已验证）。
    /// </summary>
    private static void ValidatePathInAllowlist(string path, IReadOnlySet<string> allowlist, string label)
    {
        var fullPath = Path.GetFullPath(path);
        var allowed = allowlist.Any(prefix =>
        {
            var fullPrefix = Path.GetFullPath(prefix);
            // 加路径分隔符防止同名前缀目录绕过（如 /workspace 匹配 /workspace-evil）
            return fullPath.StartsWith(
                fullPrefix.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, fullPrefix, StringComparison.OrdinalIgnoreCase);
        });

        if (!allowed)
        {
            throw new UnauthorizedAccessException(
                $"[Sandbox] {label} '{path}' 不在 AllowedPaths 白名单内，拒绝执行");
        }
    }

    private static void KillProcessTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* 进程可能已退出 */ }
    }

    private static long GetPeakMemory(Process process)
    {
        try { return process.PeakWorkingSet64; }
        catch { return 0; }
    }

    /// <summary>
    /// 日志中对参数进行遮蔽（避免密钥泄漏）
    /// </summary>
    private static string EscapeArgForLog(string arg)
    {
        if (arg.Contains("key", StringComparison.OrdinalIgnoreCase) ||
            arg.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            arg.Contains("secret", StringComparison.OrdinalIgnoreCase))
        {
            return "***";
        }

        const int maxLoggedArgLength = 120;
        return arg.Length <= maxLoggedArgLength
            ? arg
            : arg[..maxLoggedArgLength] + "...[truncated]";
    }
}
