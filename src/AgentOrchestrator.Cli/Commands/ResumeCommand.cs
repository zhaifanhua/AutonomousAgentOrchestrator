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
    public class Settings : CommandSettings
    {
        [CommandOption("-w|--workspace")]
        [Description("Workspace 根路径")]
        public string Workspace { get; set; } = "./workspace";
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"[yellow]从 state.json 恢复编排...[/] workspace={settings.Workspace}");

        var orchestrator = services.GetRequiredService<OrchestratorEngine>();

        // 需求路径传空：编排器从 workspace 下 state.json 恢复队列与进度，不创建新的初始 plan 任务
        await orchestrator.RunAsync(string.Empty, ct);

        var status = orchestrator.GetStatus();
        AnsiConsole.MarkupLine($"[bold green]恢复完成[/] 已完成={status.Completed.Count}");
        return 0;
    }
}