using AgentOrchestrator.Core.StateMachine;

namespace AgentOrchestrator.Core.Domain;

/// <summary>
/// 编排器中的原子执行单元，支持 DAG 依赖声明
/// </summary>
public record AgentTask
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// 任务类型：plan | dev | test | critique | reflect | gate
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// 输入文件路径（相对于 workspace）
    /// </summary>
    public string InputRef { get; init; } = string.Empty;

    public AgentTaskStatus Status { get; init; } = AgentTaskStatus.Init;

    /// <summary>
    /// 当前重试次数
    /// </summary>
    public int Attempt { get; init; } = 0;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; init; }

    /// <summary>
    /// 产出物引用路径列表
    /// </summary>
    public List<string> ArtifactRefs { get; init; } = [];

    /// <summary>
    /// 可扩展标签（用于路由、过滤、审计）
    /// </summary>
    public Dictionary<string, string> Tags { get; init; } = [];

    /// <summary>
    /// 父任务 ID（DAG 层次）
    /// </summary>
    public Guid? ParentTaskId { get; init; }

    /// <summary>
    /// 前置依赖任务列表（DAG 边）
    /// </summary>
    public List<Guid> DependsOn { get; init; } = [];

    /// <summary>
    /// 链路追踪 ID
    /// </summary>
    public string TraceId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 创建状态迁移后的新任务记录（不可变）
    /// </summary>
    public AgentTask WithStatus(AgentTaskStatus newStatus, string _reason = "")
    {
        StateMachineValidator.Validate(Status, newStatus);
        return this with
        {
            Status = newStatus,
            FinishedAt = StateMachineValidator.IsTerminal(newStatus) ? DateTime.UtcNow : FinishedAt
        };
    }
}

/// <summary>
/// 产出物描述
/// </summary>
public record Artifact(
    string Path,
    string Type,       // code | report | plan | log
    string Checksum,
    long SizeBytes,
    DateTime CreatedAt);

/// <summary>
/// 诊断信息（CLI 执行结果摘要）
/// </summary>
public record Diagnostics
{
    public int ExitCode { get; init; }
    public string StdErrSnippet { get; init; } = string.Empty;
    public string StdOutSnippet { get; init; } = string.Empty;
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// Token 使用量与成本估算
/// </summary>
public record TokenUsage(
    int Prompt,
    int Completion,
    string ModelId,
    double CostEstimate)
{
    public int Total => Prompt + Completion;
    public static TokenUsage Zero(string modelId) => new(0, 0, modelId, 0);
}

/// <summary>
/// Agent 执行结果
/// </summary>
public record AgentResult
{
    public bool Success { get; init; }
    public string Summary { get; init; } = string.Empty;
    public List<Artifact> Artifacts { get; init; } = [];

    /// <summary>
    /// 产生的后续任务（通过队列入队，禁止 Agent 直接调用其他 Agent）
    /// </summary>
    public List<AgentTask> NextTasks { get; init; } = [];

    public Diagnostics Diagnostics { get; init; } = new();
    public TokenUsage TokenUsage { get; init; } = TokenUsage.Zero("unknown");
    public TimeSpan WallTime { get; init; }

    /// <summary>
    /// 失败签名（标准化哈希，用于无进展检测）
    /// </summary>
    public string? FailureSignature { get; init; }

    public static AgentResult Succeed(string summary, List<AgentTask>? nextTasks = null) =>
        new() { Success = true, Summary = summary, NextTasks = nextTasks ?? [] };

    public static AgentResult Fail(string summary, string? signature = null, Diagnostics? diag = null) =>
        new() { Success = false, Summary = summary, FailureSignature = signature, Diagnostics = diag ?? new() };
}