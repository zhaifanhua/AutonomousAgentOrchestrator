using AgentOrchestrator.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AgentOrchestrator.Core.Interfaces;

/// <summary>
/// Agent 执行上下文，注入所有运行时依赖
/// </summary>
public record AgentContext(
    AgentTask Task,
    ProjectContext Project,
    IFileSystem Workspace,
    IMemoryStore Memory,
    IEventBus Events,
    ILogger Logger,
    CancellationTokenSource TimeoutToken);

/// <summary>
/// Agent 接口：声明能力、动态判断是否处理任务、执行任务
/// </summary>
public interface IAgent
{
    string Name { get; }
    string Version { get; }

    /// <summary>
    /// 声明能力标签（用于路由匹配）
    /// </summary>
    IReadOnlySet<string> Capabilities { get; }

    /// <summary>
    /// 执行任务
    /// </summary>
    Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct);

    /// <summary>
    /// 动态判断是否能处理该任务（用于路由兜底）
    /// </summary>
    Task<bool> CanHandleAsync(AgentTask task, CancellationToken ct);
}