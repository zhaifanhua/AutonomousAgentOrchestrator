using AgentOrchestrator.Core.Domain;

namespace AgentOrchestrator.Core.Interfaces;

/// <summary>
/// 智能任务路由器：基于语义嵌入 + 规则引擎决定由哪个 Agent 用哪个模型处理
/// </summary>
public interface ITaskRouter
{
    Task<RouteDecision> RouteAsync(AgentTask task, ProjectContext ctx, CancellationToken ct);
}