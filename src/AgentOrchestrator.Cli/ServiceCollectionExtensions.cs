using AgentOrchestrator.Agents.BuiltIn;
using AgentOrchestrator.Core.Interfaces;
using AgentOrchestrator.Infrastructure.EventBus;
using AgentOrchestrator.Infrastructure.LLMClients;
using AgentOrchestrator.Infrastructure.Memory;
using AgentOrchestrator.Infrastructure.Orchestration;
using AgentOrchestrator.Infrastructure.Persistence;
using AgentOrchestrator.Infrastructure.Routing;
using AgentOrchestrator.Infrastructure.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;

namespace AgentOrchestrator.Cli;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册所有编排器服务，支持 mock 模式（dry-run）
    /// </summary>
    public static IServiceCollection AddOrchestratorServices(
        this IServiceCollection services,
        string workspacePath,
        OrchestratorOptions options)
    {
        // 控制台使用人类可读文本；文件保留结构化 JSONL，便于后续检索。
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}")
            .WriteTo.File(new CompactJsonFormatter(),
                Path.Combine(workspacePath, "logs", "orchestrator.jsonl"),
                rollingInterval: RollingInterval.Day)
            .Enrich.WithProperty("workspace", workspacePath)
            .CreateLogger();

        services.AddLogging(b => b.AddSerilog(logger, dispose: true));

        // 基础设施
        services.AddSingleton<IEventBus>(sp =>
            new InMemoryEventBus(sp.GetRequiredService<ILogger<InMemoryEventBus>>()));

        services.AddSingleton<IFileSystem>(sp =>
            new WorkspaceFileSystem(workspacePath, sp.GetRequiredService<ILogger<WorkspaceFileSystem>>()));

        services.AddSingleton<IStateStore>(sp =>
            new JsonStateStore(workspacePath, sp.GetRequiredService<ILogger<JsonStateStore>>()));

        services.AddSingleton<IToolSandbox>(sp =>
            new ProcessToolSandbox(sp.GetRequiredService<ILogger<ProcessToolSandbox>>()));

        services.AddSingleton<IEmbeddingService>(sp =>
            new LocalEmbeddingService(sp.GetRequiredService<ILogger<LocalEmbeddingService>>()));

        // 语义记忆（SQLite）：工厂内同步阻塞完成异步初始化
        // AddSingleton 工厂不支持 async，用 GetAwaiter().GetResult() 确保 InitializeAsync 在首次使用前完成
        services.AddSingleton<SqliteMemoryStore>(sp =>
        {
            var store = new SqliteMemoryStore(
                Path.Combine(workspacePath, "memory.db"),
                sp.GetRequiredService<IEmbeddingService>(),
                sp.GetRequiredService<ILogger<SqliteMemoryStore>>());
            store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            return store;
        });

        services.AddSingleton<IMemoryStore>(sp => sp.GetRequiredService<SqliteMemoryStore>());

        // LLM 客户端
        if (options.UseMock)
        {
            services.AddSingleton<ILLMClient>(new MockLLMClient());
        }
        else
        {
            services.AddSingleton<ILLMClient>(sp =>
            {
                var sandbox = sp.GetRequiredService<IToolSandbox>();
                var cliTimeout = TimeSpan.FromSeconds(options.CliTimeoutSeconds);
                var clients = new List<ILLMClient>();

                // CLAUDE_CLI_PATH 优先；未设置时自动在 PATH 中查找 claude / claude.cmd
                var claudePath = Environment.GetEnvironmentVariable("CLAUDE_CLI_PATH")
                    ?? PathHelper.FindInPath("claude.cmd")
                    ?? PathHelper.FindInPath("claude");
                if (!string.IsNullOrEmpty(claudePath) && File.Exists(claudePath))
                {
                    clients.Add(new ClaudeCliClient(claudePath, sandbox,
                        sp.GetRequiredService<ILogger<ClaudeCliClient>>(),
                        cliTimeout));
                }

                // CODEX_CLI_PATH 优先；未设置时自动在 PATH 中查找 codex / codex.cmd
                var codexPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH")
                    ?? PathHelper.FindInPath("codex.cmd")
                    ?? PathHelper.FindInPath("codex");
                if (!string.IsNullOrEmpty(codexPath) && File.Exists(codexPath))
                {
                    clients.Add(new CodexCliClient(codexPath, sandbox,
                        sp.GetRequiredService<ILogger<CodexCliClient>>(),
                        cliTimeout));
                }

                // 始终加入 Mock 作为最终降级
                clients.Add(new MockLLMClient());

                return new FallbackLLMClient(clients,
                    sp.GetRequiredService<ILogger<FallbackLLMClient>>());
            });
        }

        // 路由器
        services.AddSingleton<IntelligentTaskRouter>(sp =>
            new IntelligentTaskRouter(
                sp.GetRequiredService<IEmbeddingService>(),
                sp.GetRequiredService<ILogger<IntelligentTaskRouter>>()));
        services.AddSingleton<ITaskRouter>(sp => sp.GetRequiredService<IntelligentTaskRouter>());

        // Agent 注册
        services.AddSingleton<IAgentRegistry>(sp =>
        {
            var registry = new AgentRegistry();
            var llm = sp.GetRequiredService<ILLMClient>();
            var sandbox = sp.GetRequiredService<IToolSandbox>();

            registry.Register(new PlannerAgent(llm));
            registry.Register(new DeveloperAgent(llm));
            registry.Register(new TesterAgent(llm, sandbox));
            registry.Register(new CriticAgent(llm));
            registry.Register(new ReflectorAgent(llm));
            registry.Register(new GateAgent(llm));
            return registry;
        });

        // 编排器
        var convergence = new ConvergenceConfig
        {
            MaxIterations = options.MaxIterations,
            MaxAttempts = options.MaxAttempts,
            MaxCost = options.MaxCost,
            CliTimeoutSeconds = options.CliTimeoutSeconds,
        };
        services.AddSingleton(convergence);
        services.AddSingleton<OrchestratorEngine>(sp => new OrchestratorEngine(
            sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<ITaskRouter>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IMemoryStore>(),
            convergence,
            sp.GetRequiredService<ILogger<OrchestratorEngine>>()));

        return services;
    }
}

/// <summary>
/// 在系统 PATH 中查找可执行文件，找不到返回 null。
/// </summary>
file static class PathHelper
{
    public static string? FindInPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }
}

public record OrchestratorOptions
{
    public bool UseMock { get; init; } = false;
    public int MaxIterations { get; init; } = 20;
    public int MaxAttempts { get; init; } = 3;
    public double MaxCost { get; init; } = 10.0;
    public int CliTimeoutSeconds { get; init; } = 120;
}
