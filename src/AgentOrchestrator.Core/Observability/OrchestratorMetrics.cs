using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentOrchestrator.Core.Observability;

/// <summary>
/// 编排器 OpenTelemetry 指标定义（全局单例）。
/// 使用静态构造函数强制初始化顺序：Meter 先于所有 Counter/Histogram 创建，
/// 彻底避免自动格式化工具重排字段顺序后触发的 TypeInitializationException。
/// </summary>
public static class OrchestratorMetrics
{
    public const string MeterName = "AgentOrchestrator";

    // 只读属性由静态构造函数赋值，字段声明顺序不影响初始化顺序
    public static readonly Counter<int> TasksCompleted;
    public static readonly Counter<int> TasksFailed;
    public static readonly Histogram<double> TaskDurationSeconds;
    public static readonly Histogram<double> LLMCallDurationSeconds;
    public static readonly Counter<int> LLMTokensConsumed;
    public static readonly Counter<double> LLMCostDollars;
    public static readonly Histogram<int> ConvergenceIterations;
    public static readonly Counter<int> StateTransitionErrors;
    public static readonly Counter<int> SemanticCacheHits;
    public static readonly ActivitySource ActivitySource;

    private static readonly Meter Meter;

    /// <summary>
    /// 静态构造函数：保证 Meter 最先初始化，再创建所有指标。
    /// </summary>
    static OrchestratorMetrics()
    {
        Meter = new Meter(MeterName, "1.0.0");
        ActivitySource = new ActivitySource(MeterName, "1.0.0");

        TasksCompleted = Meter.CreateCounter<int>(
            "orchestrator.tasks.completed", "tasks", "已完成任务总数");

        TasksFailed = Meter.CreateCounter<int>(
            "orchestrator.tasks.failed", "tasks", "失败任务总数");

        TaskDurationSeconds = Meter.CreateHistogram<double>(
            "orchestrator.task.duration", "s", "任务执行时长");

        LLMCallDurationSeconds = Meter.CreateHistogram<double>(
            "orchestrator.llm.call.duration", "s", "LLM 调用时长");

        LLMTokensConsumed = Meter.CreateCounter<int>(
            "orchestrator.llm.tokens.consumed", "tokens", "LLM Token 消耗");

        LLMCostDollars = Meter.CreateCounter<double>(
            "orchestrator.llm.cost", "USD", "LLM 成本估算");

        ConvergenceIterations = Meter.CreateHistogram<int>(
            "orchestrator.convergence.iterations", "iterations", "收敛所需迭代轮次");

        StateTransitionErrors = Meter.CreateCounter<int>(
            "orchestrator.state.transition.errors", "errors", "非法状态迁移次数");

        SemanticCacheHits = Meter.CreateCounter<int>(
            "orchestrator.semantic_cache.hits", "hits", "语义缓存命中次数");
    }

    /// <summary>
    /// 活跃任务数（ObservableGauge），仅注册一次
    /// </summary>
    public static void RegisterActiveTasksGauge(Func<int> valueGetter) =>
        Meter.CreateObservableGauge("orchestrator.tasks.active", valueGetter, "tasks", "当前活跃任务数");
}
