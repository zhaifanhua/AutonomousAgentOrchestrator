using AgentOrchestrator.Core.Interfaces;

namespace AgentOrchestrator.Infrastructure.Orchestration;

/// <summary>
/// Agent 注册表接口
/// </summary>
public interface IAgentRegistry
{
    IAgent? GetAgent(string name);

    void Register(IAgent agent);

    IReadOnlyList<IAgent> GetAll();
}

/// <summary>
/// 内存 Agent 注册表：通过 DI 或手动注册。
/// 支持按名称精确查找和按能力标签匹配。
/// </summary>
public class AgentRegistry : IAgentRegistry
{
    private readonly Dictionary<string, IAgent> _agents = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IAgent agent)
    {
        _agents[agent.Name] = agent;
    }

    public IAgent? GetAgent(string name)
    {
        return _agents.GetValueOrDefault(name);
    }

    public IReadOnlyList<IAgent> GetAll() => [.. _agents.Values];

    /// <summary>
    /// 按能力标签查找匹配的第一个 Agent
    /// </summary>
    public IAgent? FindByCapability(string capability) =>
        _agents.Values.FirstOrDefault(a => a.Capabilities.Contains(capability));
}