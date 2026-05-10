using AgentOrchestrator.Core.Domain;

namespace AgentOrchestrator.Core.Interfaces;

/// <summary>
/// 事件总线：编排层通信的核心枢纽，所有组件通过事件解耦
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// 发布领域事件（异步，带背压）
    /// </summary>
    Task PublishAsync<T>(T @event, CancellationToken ct) where T : DomainEvent;

    /// <summary>
    /// 订阅特定类型事件，返回取消订阅 handle
    /// </summary>
    IDisposable Subscribe<T>(Func<T, Task> handler) where T : DomainEvent;
}