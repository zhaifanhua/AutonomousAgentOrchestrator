using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Infrastructure.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace AgentOrchestrator.Cli.Commands;

/// <summary>
/// dry-run 命令：使用 MockLLMClient 执行完整流程，不消耗真实 Token。
/// 用于验证编排逻辑和配置正确性。
/// </summary>
public class DryRunCommand(IServiceProvider services) : AsyncCommand<DryRunCommand.Settings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        AnsiConsole.Write(new Rule("[bold yellow]Dry-Run[/]").RuleStyle("yellow").LeftJustified());
        AnsiConsole.MarkupLine("[grey]模式:[/] 使用 Mock LLM，不消耗真实 Token");
        AnsiConsole.MarkupLine($"[grey]工作目录:[/] {settings.Workspace}");
        AnsiConsole.MarkupLine($"[grey]需求文件:[/] {settings.RequirementRef}");
        AnsiConsole.MarkupLine($"[grey]文件日志:[/] {Path.Combine(settings.Workspace, "logs", "orchestrator.jsonl")}");

        // dry-run 使用独立 workspace 避免污染真实数据
        Directory.CreateDirectory(settings.Workspace);

        var orchestrator = services.GetRequiredService<OrchestratorEngine>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        await orchestrator.RunAsync(settings.RequirementRef, cts.Token, forceNew: true);

        var finalStatus = orchestrator.GetStatus();
        AnsiConsole.Write(BuildStatusPanel(finalStatus));
        AnsiConsole.MarkupLine("[green]Dry-run 完成[/]");
        return 0;
    }

    private static Panel BuildStatusPanel(OrchestratorState state) =>
        new Panel(
            $"完成: [green]{state.Completed.Count}[/]  失败: [red]{state.Failed.Count}[/]  " +
            $"轮次: {state.Convergence.CurrentIteration}/{state.Convergence.MaxIterations}"
        ).Header("[bold]Dry-Run 状态[/]");

    public class Settings : CommandSettings
    {
        [CommandOption("-w|--workspace")]
        [Description("Workspace 根路径")]
        public string Workspace { get; set; } = "./workspace/dry-run";

        [CommandOption("-r|--requirement")]
        [Description("需求文件路径")]
        public string RequirementRef { get; set; } = "requirements.md";

        [CommandOption("--max-iterations")]
        [Description("最大迭代轮次（dry-run 默认 3）")]
        public int MaxIterations { get; set; } = 3;
    }
}
