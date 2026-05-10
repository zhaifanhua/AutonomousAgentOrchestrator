using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Infrastructure.EventBus;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentOrchestrator.Tests;

public class InMemoryEventBusTests
{
    private readonly InMemoryEventBus _bus = new(NullLogger<InMemoryEventBus>.Instance);

    [Fact]
    public async Task PublishAsync_ShouldInvokeSubscriber()
    {
        var received = new List<TaskDequeued>();
        _bus.Subscribe<TaskDequeued>(e => { received.Add(e); return Task.CompletedTask; });

        var @event = new TaskDequeued(Guid.NewGuid(), "plan", DateTime.UtcNow);
        await _bus.PublishAsync(@event, CancellationToken.None);

        Assert.Single(received);
        Assert.Equal(@event.TaskId, received[0].TaskId);
    }

    [Fact]
    public async Task Unsubscribe_ShouldStopReceivingEvents()
    {
        var count = 0;
        var handle = _bus.Subscribe<TaskDequeued>(_ => { count++; return Task.CompletedTask; });

        await _bus.PublishAsync(new TaskDequeued(Guid.NewGuid(), "plan", DateTime.UtcNow), CancellationToken.None);
        handle.Dispose();
        await _bus.PublishAsync(new TaskDequeued(Guid.NewGuid(), "dev", DateTime.UtcNow), CancellationToken.None);

        Assert.Equal(1, count);  // 只收到第一个
    }

    [Fact]
    public async Task HandlerException_ShouldNotPropagateToPublisher()
    {
        _bus.Subscribe<TaskDequeued>(_ => throw new Exception("故意抛出"));
        var ex = await Record.ExceptionAsync(() =>
            _bus.PublishAsync(new TaskDequeued(Guid.NewGuid(), "plan", DateTime.UtcNow), CancellationToken.None));
        Assert.Null(ex);
    }
}
