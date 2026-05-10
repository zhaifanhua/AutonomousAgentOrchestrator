using AgentOrchestrator.Core.Domain;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;

namespace AgentOrchestrator.Cli.Commands;

/// <summary>
/// status 命令：读取并展示 state.json 当前状态
/// </summary>
public class StatusCommand(IServiceProvider services) : AsyncCommand<StatusCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("-w|--workspace")]
        [Description("Workspace 根路径")]
        public string Workspace { get; set; } = "./workspace";

        [CommandOption("--json")]
        [Description("以 JSON 格式输出")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var stateFile = Path.Combine(settings.Workspace, "state.json");
        if (!File.Exists(stateFile))
        {
            AnsiConsole.MarkupLine("[red]state.json 不存在，请先运行 run 命令[/]");
            return 1;
        }

        var json = await File.ReadAllTextAsync(stateFile);
        var state = JsonSerializer.Deserialize<OrchestratorState>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (state == null)
        {
            AnsiConsole.MarkupLine("[red]state.json 解析失败[/]");
            return 1;
        }

        if (settings.Json)
        {
            Console.WriteLine(json);
            return 0;
        }

        // 富文本展示
        var table = new Table()
            .Title("[bold]Orchestrator 状态[/]")
            .AddColumn("字段").AddColumn("值");

        table.AddRow("版本", state.Version.ToString());
        table.AddRow("开始时间", state.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        table.AddRow("最后保存", state.LastSavedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        table.AddRow("待执行队列", $"[yellow]{state.Queue.Count}[/] 个任务");
        table.AddRow("已完成", $"[green]{state.Completed.Count}[/] 个任务");
        table.AddRow("已失败", $"[red]{state.Failed.Count}[/] 个任务");
        table.AddRow("当前迭代轮次", $"{state.Convergence.CurrentIteration} / {state.Convergence.MaxIterations}");
        table.AddRow("Token 消耗", $"{state.Budget.TotalTokensUsed:N0} / {state.Budget.MaxTokens:N0}");
        table.AddRow("预计成本", $"${state.Budget.TotalCostUsed:F4} / ${state.Budget.MaxCost:F2}");

        AnsiConsole.Write(table);

        if (state.Queue.Any())
        {
            var queueTable = new Table()
                .Title("[yellow]待执行队列[/]")
                .AddColumn("ID").AddColumn("类型").AddColumn("尝试次数").AddColumn("输入");

            foreach (var task in state.Queue.Take(10))
                queueTable.AddRow(task.Id.ToString("N")[..8], task.Type, task.Attempt.ToString(), task.InputRef);

            AnsiConsole.Write(queueTable);
        }

        return 0;
    }
}