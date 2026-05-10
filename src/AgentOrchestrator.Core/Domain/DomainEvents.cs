using AgentOrchestrator.Core.StateMachine;

namespace AgentOrchestrator.Core.Domain;

/// <summary>
/// 所有领域事件的基类
/// </summary>
public abstract record DomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
    public string TraceId { get; init; } = string.Empty;
}

/// <summary>
/// 任务从队列中取出
/// </summary>
public record TaskDequeued(Guid TaskId, string Type, DateTime At) : DomainEvent;

/// <summary>
/// Agent 开始执行任务
/// </summary>
public record AgentExecutionStarted(Guid TaskId, string AgentType, DateTime At) : DomainEvent;

/// <summary>
/// Agent 执行完成
/// </summary>
public record AgentExecutionCompleted(Guid TaskId, AgentResult Result, TimeSpan Duration) : DomainEvent;

/// <summary>
/// 任务状态发生迁移
/// </summary>
public record StateTransitioned(
    Guid TaskId,
    AgentTaskStatus From,
    AgentTaskStatus To,
    string Reason) : DomainEvent;

/// <summary>
/// 收敛检查被触发
/// </summary>
public record ConvergenceCheckTriggered(
    int Iteration,
    bool NoProgress,
    string Signature) : DomainEvent;

/// <summary>
/// 预算（Token 或成本）超限
/// </summary>
public record BudgetExceeded(
    string LimitType,
    double Current,
    double Max) : DomainEvent;

/// <summary>
/// LLM 调用完成
/// </summary>
public record LLMCallCompleted(
    Guid TaskId,
    string ModelId,
    int PromptTokens,
    int CompletionTokens,
    double CostEstimate,
    TimeSpan Duration) : DomainEvent;

/// <summary>
/// 路由决策完成
/// </summary>
public record TaskRouted(
    Guid TaskId,
    string AgentType,
    string ModelId,
    float Confidence) : DomainEvent;