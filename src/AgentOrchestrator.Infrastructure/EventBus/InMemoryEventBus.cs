using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AgentOrchestrator.Infrastructure.EventBus;

/// <summary>
/// 基于内存的事件总线实现。
/// 使用 ConcurrentDictionary 管理订阅者，支持多订阅者并发调用。
/// </summary>
public class InMemoryEventBus(ILogger<InMemoryEventBus> logger) : IEventBus
{
    // 事件类型 → 处理器列表（Func 包装为 object 避免泛型集合问题）
    private readonly ConcurrentDictionary<Type, List<object>> _handlers = new();

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task PublishAsync<T>(T @event, CancellationToken ct) where T : DomainEvent
    {
        var eventType = typeof(T);
        if (!_handlers.TryGetValue(eventType, out var handlers))
            return;

        List<object> snapshot;
        await _lock.WaitAsync(ct);
        try
        {
            snapshot = [.. handlers];
        }
        finally
        {
            _lock.Release();
        }

        var tasks = snapshot
            .OfType<Func<T, Task>>()
            .Select(h => InvokeHandler(h, @event));

        await Task.WhenAll(tasks);
    }

    private async Task InvokeHandler<T>(Func<T, Task> handler, T @event) where T : DomainEvent
    {
        try
        {
            await handler(@event);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "事件处理器异常，事件类型={EventType}", typeof(T).Name);
        }
    }

    public IDisposable Subscribe<T>(Func<T, Task> handler) where T : DomainEvent
    {
        var eventType = typeof(T);
        _lock.Wait();
        try
        {
            _handlers.GetOrAdd(eventType, _ => []).Add(handler);
        }
        finally
        {
            _lock.Release();
        }
        return new SubscriptionHandle(() => Unsubscribe(eventType, (object)handler));
    }

    private void Unsubscribe(Type eventType, object handler)
    {
        _lock.Wait();
        try
        {
            if (_handlers.TryGetValue(eventType, out var handlers))
                handlers.Remove(handler);
        }
        finally
        {
            _lock.Release();
        }
    }

    private class SubscriptionHandle(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}