using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentOrchestrator.Core.Observability;

/// <summary>
/// 编排器 OpenTelemetry 指标定义（全局单例）
/// </summary>
public static class OrchestratorMetrics
{
    public const string MeterName = "AgentOrchestrator";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>
    /// 已完成任务总数
    /// </summary>
    public static readonly Counter<int> TasksCompleted =
        Meter.CreateCounter<int>("orchestrator.tasks.completed", "tasks", "已完成任务总数");

    /// <summary>
    /// 失败任务总数
    /// </summary>
    public static readonly Counter<int> TasksFailed =
        Meter.CreateCounter<int>("orchestrator.tasks.failed", "tasks", "失败任务总数");

    /// <summary>
    /// 任务端到端执行时长（秒）
    /// </summary>
    public static readonly Histogram<double> TaskDurationSeconds =
        Meter.CreateHistogram<double>("orchestrator.task.duration", "s", "任务执行时长");

    /// <summary>
    /// LLM 调用时长（秒）
    /// </summary>
    public static readonly Histogram<double> LLMCallDurationSeconds =
        Meter.CreateHistogram<double>("orchestrator.llm.call.duration", "s", "LLM 调用时长");

    /// <summary>
    /// LLM Token 消耗量
    /// </summary>
    public static readonly Counter<int> LLMTokensConsumed =
        Meter.CreateCounter<int>("orchestrator.llm.tokens.consumed", "tokens", "LLM Token 消耗");

    /// <summary>
    /// LLM 成本估算（美元）
    /// </summary>
    public static readonly Counter<double> LLMCostDollars =
        Meter.CreateCounter<double>("orchestrator.llm.cost", "USD", "LLM 成本估算");

    /// <summary>
    /// 收敛迭代轮次分布
    /// </summary>
    public static readonly Histogram<int> ConvergenceIterations =
        Meter.CreateHistogram<int>("orchestrator.convergence.iterations", "iterations", "收敛所需迭代轮次");

    /// <summary>
    /// 状态迁移错误计数（非法迁移）
    /// </summary>
    public static readonly Counter<int> StateTransitionErrors =
        Meter.CreateCounter<int>("orchestrator.state.transition.errors", "errors", "非法状态迁移次数");

    /// <summary>
    /// 语义缓存命中次数
    /// </summary>
    public static readonly Counter<int> SemanticCacheHits =
        Meter.CreateCounter<int>("orchestrator.semantic_cache.hits", "hits", "语义缓存命中次数");

    /// <summary>
    /// 活跃任务数（ObservableGauge）
    /// </summary>
    public static void RegisterActiveTasksGauge(Func<int> valueGetter) =>
        Meter.CreateObservableGauge("orchestrator.tasks.active", valueGetter, "tasks", "当前活跃任务数");

    /// <summary>
    /// ActivitySource 用于链路追踪
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(MeterName, "1.0.0");
}