using AgentOrchestrator.Core.Domain;

namespace AgentOrchestrator.Core.Interfaces;

/// <summary>
/// 状态持久化接口：原子写入，支持乐观锁和崩溃恢复
/// </summary>
public interface IStateStore
{
    /// <summary>
    /// 加载状态（若文件不存在返回 null）
    /// </summary>
    Task<OrchestratorState?> LoadAsync(CancellationToken ct);

    /// <summary>
    /// 原子保存状态（临时文件 → 校验 → File.Move）
    /// </summary>
    Task SaveAsync(OrchestratorState state, CancellationToken ct);

    /// <summary>
    /// 获取当前版本号（用于乐观锁冲突检测）
    /// </summary>
    Task<long> GetVersionAsync(CancellationToken ct);
}