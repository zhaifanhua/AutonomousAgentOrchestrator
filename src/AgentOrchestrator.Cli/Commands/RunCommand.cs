using AgentOrchestrator.Infrastructure.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace AgentOrchestrator.Cli.Commands;

/// <summary>
/// run 命令：从头启动编排器
/// </summary>
public class RunCommand(IServiceProvider services) : AsyncCommand<RunCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("-w|--workspace")]
        [Description("Workspace 根路径")]
        public string Workspace { get; set; } = "./workspace";

        [CommandOption("-r|--requirement")]
        [Description("需求文件路径（相对于 workspace）")]
        public string RequirementRef { get; set; } = "requirements.md";

        [CommandOption("--max-iterations")]
        [Description("最大迭代轮次")]
        public int MaxIterations { get; set; } = 20;

        [CommandOption("--max-attempts")]
        [Description("单任务最大重试次数")]
        public int MaxAttempts { get; set; } = 3;

        [CommandOption("--max-cost")]
        [Description("最大成本（美元）")]
        public double MaxCost { get; set; } = 10.0;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        AnsiConsole.Write(new FigletText("Agent Orchestrator").Color(Color.Aqua));
        AnsiConsole.MarkupLine($"[green]工作目录:[/] {settings.Workspace}");
        AnsiConsole.MarkupLine($"[green]需求文件:[/] {settings.RequirementRef}");

        var orchestrator = services.GetRequiredService<OrchestratorEngine>();
        await AnsiConsole.Status()
            .StartAsync("编排器运行中...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                await orchestrator.RunAsync(settings.RequirementRef, ct);
            });

        var status = orchestrator.GetStatus();
        AnsiConsole.MarkupLine($"\n[bold green]完成[/] 已完成={status.Completed.Count} 失败={status.Failed.Count}");
        return 0;
    }
}