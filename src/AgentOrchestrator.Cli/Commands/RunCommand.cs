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
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        AnsiConsole.Write(new Rule("[bold aqua]Agent Orchestrator[/]").RuleStyle("aqua").LeftJustified());
        AnsiConsole.MarkupLine($"[grey]工作目录:[/] {settings.Workspace}");
        AnsiConsole.MarkupLine($"[grey]需求文件:[/] {settings.RequirementRef}");
        AnsiConsole.MarkupLine($"[grey]文件日志:[/] {Path.Combine(settings.Workspace, "logs", "orchestrator.jsonl")}");

        var orchestrator = services.GetRequiredService<OrchestratorEngine>();
        await orchestrator.RunAsync(settings.RequirementRef, ct, forceNew: true);

        var status = orchestrator.GetStatus();
        AnsiConsole.MarkupLine($"[bold green]完成[/] 已完成={status.Completed.Count} 失败={status.Failed.Count}");
        return 0;
    }

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

        [CommandOption("--cli-timeout-seconds")]
        [Description("单次 LLM CLI 调用超时秒数")]
        public int CliTimeoutSeconds { get; set; } = 120;
    }
}
