using AgentOrchestrator.Cli;
using AgentOrchestrator.Cli.Commands;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

using System.Globalization;
using System.Text;

// 控制台 UTF-8 + 中文 UI（帮助、选项说明与系统中文资源一致）
Console.OutputEncoding = Encoding.UTF8;
var zh = CultureInfo.GetCultureInfo("zh-CN");
CultureInfo.DefaultThreadCurrentCulture = zh;
CultureInfo.DefaultThreadCurrentUICulture = zh;

// 解析 workspace 参数（在 DI 注册前需要确定路径）
var workspace = GetArg(args, "--workspace", "-w") ?? "./workspace";
var useMock = ArgsContainsCommand(args, "dry-run") || ArgsContainsCommand(args, "--mock");
var maxIter = int.TryParse(GetArg(args, "--max-iterations"), out var mi) ? mi : 20;
var maxAttempts = int.TryParse(GetArg(args, "--max-attempts"), out var ma) ? ma : 3;
var maxCost = double.TryParse(GetArg(args, "--max-cost"), out var mc) ? mc : 10.0;
var cliTimeout = int.TryParse(GetArg(args, "--cli-timeout-seconds"), out var cts) ? cts : 120;

// 仅在实际执行子命令时创建 workspace（避免 --help / 无参数查看用法时污染磁盘）
if (InvokesOrchestratorCommand(args))
{
    Directory.CreateDirectory(workspace);
}

// 配置 DI
var services = new ServiceCollection();
services.AddOrchestratorServices(workspace, new OrchestratorOptions
{
    UseMock = useMock,
    MaxIterations = maxIter,
    MaxAttempts = maxAttempts,
    MaxCost = maxCost,
    CliTimeoutSeconds = cliTimeout,
});

// 注册命令（将 IServiceProvider 注入命令构造器）
services.AddTransient<RunCommand>(sp => new RunCommand(sp));
services.AddTransient<ResumeCommand>(sp => new ResumeCommand(sp));
services.AddTransient<StatusCommand>(sp => new StatusCommand(sp));
services.AddTransient<DryRunCommand>(sp => new DryRunCommand(sp));

var registrar = new TypeRegistrar(services);

var app = new CommandApp(registrar);
app.Configure(config =>
{
    config.SetApplicationName("aao");
    config.SetApplicationVersion("1.0.0");
    config.AddExample("run", "--workspace", "./myproject", "--requirement", "requirements.md");
    config.AddExample("resume", "--workspace", "./myproject");
    config.AddExample("status", "--workspace", "./myproject");
    config.AddExample("dry-run", "--workspace", "./myproject/dry-run");

    config.AddCommand<RunCommand>("run")
        .WithDescription("从头启动编排器执行任务");

    config.AddCommand<ResumeCommand>("resume")
        .WithDescription("从 state.json 恢复中断的编排");

    config.AddCommand<StatusCommand>("status")
        .WithDescription("查看当前编排器状态");

    config.AddCommand<DryRunCommand>("dry-run")
        .WithDescription("使用 Mock LLM 执行 dry-run（不消耗真实 Token）");
});

return await app.RunAsync(args);

// 辅助：解析命名参数值
static string? GetArg(string[] args, params string[] names)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (names.Contains(args[i]))
        {
            return args[i + 1];
        }
    }

    return null;
}

/// <summary>
/// 是否为会读写 workspace 的子命令（run / resume / status / dry-run）。
/// </summary>
static bool InvokesOrchestratorCommand(string[] args)
{
    ReadOnlySpan<string> cmds = ["run", "resume", "status", "dry-run"];
    foreach (var a in args)
    {
        foreach (var c in cmds)
        {
            if (string.Equals(a, c, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
    }

    return false;
}

/// <summary>
/// 参数中是否出现指定子命令或开关（整词匹配）。
/// </summary>
static bool ArgsContainsCommand(string[] args, string token)
{
    foreach (var a in args)
    {
        if (string.Equals(a, token, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}
