namespace AgentOrchestrator.Core.Domain;

/// <summary>
/// 已知缺陷条目
/// </summary>
public record KnownDefect(
    string Id,
    string Description,
    string Severity,   // low | medium | high | critical
    string? RelatedFile);

/// <summary>
/// 变更摘要（用于增量路由决策）
/// </summary>
public record ChangeDigest(
    string DiffHash,
    int FilesChanged,
    int LinesAdded,
    int LinesDeleted,
    string Summary);

/// <summary>
/// 项目上下文：在整个编排生命周期内共享
/// </summary>
public record ProjectContext
{
    public string RequirementSummary { get; init; } = string.Empty;

    /// <summary>
    /// 允许 Agent 读写的路径白名单
    /// </summary>
    public List<string> PathsAllowlist { get; init; } = [];

    public List<KnownDefect> KnownDefects { get; init; } = [];
    public ChangeDigest? LastChangeDigest { get; init; }

    /// <summary>
    /// 文件级复杂度评分（用于路由决策）
    /// </summary>
    public Dictionary<string, double> ComplexityScores { get; init; } = [];

    /// <summary>
    /// Workspace 根路径（绝对路径）
    /// </summary>
    public string WorkspacePath { get; init; } = string.Empty;
}