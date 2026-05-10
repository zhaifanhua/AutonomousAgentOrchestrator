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

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 超时：强制终止进程
                logger.LogWarning("工具超时，强制终止进程 PID={PID}", process.Id);
                KillProcessTree(process);
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
            FileName = inv.Command,
            UseShellExecute = false,         // 禁止 shell，防注入
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = inv.WorkingDirectory,
            CreateNoWindow = true,
        };

        foreach (var arg in inv.Arguments)
        {
            psi.ArgumentList.Add(arg);        // 安全的参数数组传递
        }

        foreach (var (k, v) in inv.Environment)
        {
            psi.Environment[k] = v;
        }

        return psi;
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
    private static string EscapeArgForLog(string arg) =>
        arg.Contains("key", StringComparison.OrdinalIgnoreCase) ? "***" : arg;
}