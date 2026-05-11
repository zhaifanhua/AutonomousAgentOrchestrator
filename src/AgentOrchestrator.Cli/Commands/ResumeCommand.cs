using AgentOrchestrator.Infrastructure.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace AgentOrchestrator.Cli.Commands;

/// <summary>
/// resume 命令：从 state.json 恢复中断的编排
/// </summary>
public class ResumeCommand(IServiceProvider services) : AsyncCommand<ResumeCommand.Settings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        AnsiConsole.Write(new Rule("[bold yellow]恢复编排[/]").RuleStyle("yellow").LeftJustified());
        AnsiConsole.MarkupLine($"[grey]工作目录:[/] {settings.Workspace}");
        AnsiConsole.MarkupLine($"[grey]文件日志:[/] {Path.Combine(settings.Workspace, "logs", "orchestrator.jsonl")}");

        var orchestrator = services.GetRequiredService<OrchestratorEngine>();

        // 需求路径传空：编排器从 workspace 下 state.json 恢复队列与进度，不创建新的初始 plan 任务
        await orchestrator.RunAsync(string.Empty, ct);

        var status = orchestrator.GetStatus();
        AnsiConsole.MarkupLine($"[bold green]恢复完成[/] 已完成={status.Completed.Count} 失败={status.Failed.Count}");
        return 0;
    }

    public class Settings : CommandSettings
    {
        [CommandOption("-w|--workspace")]
        [Description("Workspace 根路径")]
        public string Workspace { get; set; } = "./workspace";
    }
}
