namespace AgentOrchestrator.Core.Domain;

/// <summary>
/// 收敛追踪状态
/// </summary>
public record ConvergenceState
{
    public int CurrentIteration { get; init; } = 0;
    public int MaxIterations { get; init; } = 20;

    /// <summary>
    /// 连续相同失败签名计数（用于无进展检测）
    /// </summary>
    public int NoProgressCount { get; init; } = 0;

    /// <summary>
    /// 最近一次失败签名
    /// </summary>
    public string? LastFailureSignature { get; init; }

    /// <summary>
    /// 历史成功率（用于自适应收敛）
    /// </summary>
    public double HistoricalSuccessRate { get; init; } = 1.0;

    /// <summary>
    /// 自适应最大轮次（动态调整）
    /// </summary>
    public int AdaptiveMaxAttempts { get; init; } = 3;
}

/// <summary>
/// Token / 成本预算状态
/// </summary>
public record BudgetState
{
    public int TotalTokensUsed { get; init; } = 0;
    public double TotalCostUsed { get; init; } = 0.0;
    public int MaxTokens { get; init; } = 1_000_000;
    public double MaxCost { get; init; } = 10.0;
    public int MaxTokensPerTask { get; init; } = 50_000;

    public bool IsTokenBudgetExceeded => TotalTokensUsed >= MaxTokens;
    public bool IsCostBudgetExceeded => TotalCostUsed >= MaxCost;
    public bool IsExceeded => IsTokenBudgetExceeded || IsCostBudgetExceeded;
}

/// <summary>
/// 编排器完整可持久化状态（文件系统单一事实来源）
/// </summary>
public record OrchestratorState
{
    /// <summary>
    /// 乐观锁版本号，每次保存递增
    /// </summary>
    public long Version { get; init; } = 0;

    /// <summary>
    /// 状态文件 Schema 版本（用于向前兼容迁移）
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    public List<AgentTask> Queue { get; init; } = [];
    public List<AgentTask> Completed { get; init; } = [];
    public List<AgentTask> Failed { get; init; } = [];

    public ProjectContext Project { get; init; } = new();
    public ConvergenceState Convergence { get; init; } = new();
    public BudgetState Budget { get; init; } = new();

    public DateTime LastSavedAt { get; init; } = DateTime.UtcNow;
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    public OrchestratorState IncrementVersion() => this with { Version = Version + 1, LastSavedAt = DateTime.UtcNow };
}