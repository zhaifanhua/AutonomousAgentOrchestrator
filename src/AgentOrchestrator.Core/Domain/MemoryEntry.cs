namespace AgentOrchestrator.Core.Domain;

/// <summary>
/// 记忆条目类型
/// </summary>
public enum MemoryType
{
    Fact,
    Decision,
    Lesson,
    ErrorPattern
}

/// <summary>
/// 记忆状态
/// </summary>
public enum MemoryStatus
{
    Active,

    /// <summary>
    /// 被更新的事实取代（矛盾检测后标记）
    /// </summary>
    Superseded,

    Archived
}

/// <summary>
/// 语义记忆条目（带衰减因子和置信度）
/// </summary>
public record MemoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// 向量嵌入（SQLite blob 存储）
    /// </summary>
    public ReadOnlyMemory<float> Embedding { get; init; }

    public MemoryType Type { get; init; }
    public MemoryStatus Status { get; init; } = MemoryStatus.Active;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; init; } = DateTime.UtcNow;
    public int AccessCount { get; init; } = 0;

    /// <summary>
    /// 指数衰减系数（控制记忆权重随时间的衰减速度）
    /// </summary>
    public double DecayFactor { get; init; } = 0.01;

    /// <summary>
    /// 置信度 [0,1]，低于阈值不参与推理
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    public List<string> Tags { get; init; } = [];

    /// <summary>
    /// 关联的 TaskId
    /// </summary>
    public Guid? RelatedTaskId { get; init; }

    /// <summary>
    /// 计算当前权重（访问次数 × 指数衰减）
    /// </summary>
    public double ComputeWeight()
    {
        var daysSince = (DateTime.UtcNow - CreatedAt).TotalDays;
        return AccessCount * Math.Exp(-DecayFactor * daysSince);
    }
}