using AgentOrchestrator.Agents.Base;
using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentOrchestrator.Agents.BuiltIn;

/// <summary>
/// Gate Agent：人工审批检查点。
/// 在控制台显示当前状态，等待用户交互式确认。
/// CI 环境下可通过环境变量 GATE_AUTO_APPROVE=true 自动通过。
/// </summary>
public class GateAgent(ILLMClient llmClient) : AgentBase(llmClient)
{
    public override string Name => "Gate";
    public override string Version => "1.0";
    public override IReadOnlySet<string> Capabilities => new HashSet<string> { "gate" };

    public override async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        // CI 环境自动通过
        var autoApprove = Environment.GetEnvironmentVariable("GATE_AUTO_APPROVE") == "true";
        if (autoApprove)
        {
            ctx.Logger.LogInformation("Gate: CI 自动审批通过");
            return AgentResult.Succeed("gate:approved");
        }

        // 显示摘要并等待用户输入
        var summary = ctx.Workspace.Exists(ctx.Task.InputRef)
            ? await ctx.Workspace.ReadAsync(ctx.Task.InputRef, ct)
            : "无摘要";

        Console.WriteLine();
        Console.WriteLine("═══════════════════════ 人工审批检查点 ═══════════════════════");
        Console.WriteLine($"任务 ID  : {ctx.Task.Id}");
        Console.WriteLine($"任务类型 : {ctx.Task.Type}");
        Console.WriteLine($"摘要:\n{summary[..Math.Min(500, summary.Length)]}");
        Console.WriteLine("══════════════════════════════════════════════════════════════");
        Console.Write("请选择 [a=批准 / r=拒绝 / c=请求修改]: ");

        var input = await ReadLineWithCancellationAsync(ct);
        return (input?.Trim().ToLowerInvariant()) switch
        {
            "a" => AgentResult.Succeed("gate:approved"),
            "r" => AgentResult.Fail("gate:rejected"),
            "c" => AgentResult.Succeed("gate:changes_requested"),
            _ => AgentResult.Fail($"gate:unknown_input:{input}")
        };
    }

    private static async Task<string?> ReadLineWithCancellationAsync(CancellationToken ct)
    {
        return await Task.Run(Console.ReadLine, ct);
    }
}