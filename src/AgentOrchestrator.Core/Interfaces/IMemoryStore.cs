using AgentOrchestrator.Core.Domain;

namespace AgentOrchestrator.Core.Interfaces;

/// <summary>
/// 语义记忆存储：支持向量检索、衰减、矛盾检测和压缩
/// </summary>
public interface IMemoryStore
{
    /// <summary>
    /// 写入记忆（自动生成嵌入向量）
    /// </summary>
    Task StoreAsync(MemoryEntry entry, CancellationToken ct);

    /// <summary>
    /// 语义检索最相关的 topK 条记忆
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> RecallAsync(string query, int topK, CancellationToken ct);

    /// <summary>
    /// 删除特定记忆条目
    /// </summary>
    Task ForgetAsync(Guid entryId, CancellationToken ct);

    /// <summary>
    /// 压缩：合并相似记忆、清除低权重记忆、标记矛盾记忆
    /// </summary>
    Task CompactAsync(CancellationToken ct);

    /// <summary>
    /// 按标签检索
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> RecallByTagAsync(string tag, CancellationToken ct);
}