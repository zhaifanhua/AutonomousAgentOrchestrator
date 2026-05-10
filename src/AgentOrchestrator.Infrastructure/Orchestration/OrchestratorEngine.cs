using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using AgentOrchestrator.Core.Observability;
using AgentOrchestrator.Core.StateMachine;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace AgentOrchestrator.Infrastructure.Orchestration;

/// <summary>
/// 核心编排器：事件驱动管道的主循环。
/// - System.Threading.Channels 有界通道 + 信号量控制并行度
/// - 单写者模型保证状态写入安全
/// - 收敛检测：最大轮次 / 无进展 / 预算超限 / 时间超限
/// - 自适应收敛参数根据历史成功率动态调整
/// </summary>
public class OrchestratorEngine(
    IStateStore stateStore,
    ITaskRouter router,
    IAgentRegistry agentRegistry,
    IEventBus eventBus,
    IFileSystem workspace,
    IMemoryStore memory,
    ConvergenceConfig convergence,
    ILogger<OrchestratorEngine> logger)
{
    // 有界通道，背压保护（最多 100 任务在队列中）
    private readonly Channel<AgentTask> _taskChannel =
        Channel.CreateBounded<AgentTask>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });

    // 并行度信号量（同时最多 3 个 Agent 执行）
    private readonly SemaphoreSlim _parallelSem = new(3, 3);

    private OrchestratorState _state = new();
    private readonly object _stateLock = new();
    private int _activeTaskCount = 0;

    public async Task RunAsync(string requirementRef, CancellationToken ct)
    {
        logger.LogInformation("编排器启动，需求文件: {Ref}", requirementRef);
        _state = await stateStore.LoadAsync(ct) ?? new OrchestratorState
        {
            Project = new ProjectContext
            {
                WorkspacePath = workspace.RootPath,
                RequirementSummary = requirementRef,
                PathsAllowlist = ["src/", "tests/", "plans/", "reports/"]
            }
        };

        OrchestratorMetrics.RegisterActiveTasksGauge(() => _activeTaskCount);

        // 初始任务入队
        if (!_state.Queue.Any() && !_state.Completed.Any())
        {
            await EnqueueAsync(new AgentTask
            {
                Type = "plan",
                InputRef = requirementRef,
                Tags = new Dictionary<string, string> { ["initial"] = "true" }
            }, ct);
        }
        else
        {
            // resume：恢复队列
            foreach (var task in _state.Queue)
                await _taskChannel.Writer.WriteAsync(task, ct);
        }

        await ProcessLoopAsync(ct);
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        await foreach (var task in _taskChannel.Reader.ReadAllAsync(ct))
        {
            // 收敛检测（每次出队前检查）
            if (!CheckConvergence(task)) break;

            // 预算检查
            if (_state.Budget.IsExceeded)
            {
                logger.LogWarning("预算超限，进入 PausedForApproval");
                await PublishBudgetExceededAsync(ct);
                break;
            }

            // 依赖检查：DependsOn 任务未完成则跳过（放回队尾）
            if (!AreDependenciesMet(task))
            {
                await _taskChannel.Writer.WriteAsync(task with { }, ct);
                continue;
            }

            _ = ExecuteTaskAsync(task, ct);  // 非阻塞执行（信号量控制并行度）
        }

        _taskChannel.Writer.TryComplete();
        logger.LogInformation("编排器结束，完成={Done} 失败={Failed}",
            _state.Completed.Count, _state.Failed.Count);
    }

    private async Task ExecuteTaskAsync(AgentTask task, CancellationToken ct)
    {
        await _parallelSem.WaitAsync(ct);
        Interlocked.Increment(ref _activeTaskCount);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var activity = OrchestratorMetrics.ActivitySource.StartActivity("Task.Execute");
        activity?.SetTag("taskId", task.Id.ToString());
        activity?.SetTag("taskType", task.Type);

        try
        {
            await TransitionStateAsync(task, AgentTaskStatus.Plan, "开始执行", ct);
            await eventBus.PublishAsync(new TaskDequeued(task.Id, task.Type, DateTime.UtcNow), ct);

            // 路由决策
            var route = await router.RouteAsync(task, _state.Project, ct);
            await eventBus.PublishAsync(
                new TaskRouted(task.Id, route.AgentType, route.ModelId, route.Confidence), ct);

            // 查找 Agent
            var agent = agentRegistry.GetAgent(route.AgentType)
                ?? throw new InvalidOperationException($"未找到 Agent: {route.AgentType}");

            // 注入模型选择到 Tags
            var enrichedTask = task with
            {
                Tags = new Dictionary<string, string>(task.Tags) { ["modelId"] = route.ModelId }
            };

            var agentCtx = new AgentContext(enrichedTask, _state.Project, workspace, memory, eventBus,
                logger, new CancellationTokenSource(TimeSpan.FromSeconds(convergence.CliTimeoutSeconds)));

            await eventBus.PublishAsync(
                new AgentExecutionStarted(task.Id, route.AgentType, DateTime.UtcNow), ct);

            // 单任务超时 Token
            using var taskTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            taskTimeout.CancelAfter(TimeSpan.FromSeconds(convergence.TaskTimeoutSeconds));

            var result = await agent.ExecuteAsync(agentCtx, taskTimeout.Token);
            sw.Stop();

            await eventBus.PublishAsync(
                new AgentExecutionCompleted(task.Id, result, sw.Elapsed), ct);

            OrchestratorMetrics.TaskDurationSeconds.Record(sw.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("agentType", route.AgentType));

            await HandleResultAsync(task, result, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("任务超时: {TaskId}", task.Id);
            await HandleTimeoutAsync(task, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "任务执行异常: {TaskId}", task.Id);
            await HandleFailureAsync(task, ex.Message, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _activeTaskCount);
            _parallelSem.Release();
        }
    }

    private async Task HandleResultAsync(AgentTask task, AgentResult result, CancellationToken ct)
    {
        if (result.Success)
        {
            OrchestratorMetrics.TasksCompleted.Add(1, new KeyValuePair<string, object?>("agentType", task.Type));
            await TransitionStateAsync(task, AgentTaskStatus.Done, "执行成功", ct);
            MoveToCompleted(task with { Status = AgentTaskStatus.Done });

            // 将后续任务入队
            foreach (var next in result.NextTasks)
                await EnqueueAsync(next, ct);

            // 无后续任务且全部完成 → 关闭通道
            if (!result.NextTasks.Any() && _taskChannel.Reader.Count == 0 && _activeTaskCount <= 1)
                _taskChannel.Writer.TryComplete();
        }
        else
        {
            await HandleFailureWithRetryAsync(task, result, ct);
        }

        await SaveStateAsync(ct);
    }

    private async Task HandleFailureWithRetryAsync(AgentTask task, AgentResult result, CancellationToken ct)
    {
        OrchestratorMetrics.TasksFailed.Add(1, new KeyValuePair<string, object?>("agentType", task.Type));

        var newAttempt = task.Attempt + 1;

        // 无进展检测：签名相同则计数+1
        if (result.FailureSignature != null &&
            result.FailureSignature == _state.Convergence.LastFailureSignature)
        {
            var noProgress = _state.Convergence.NoProgressCount + 1;
            await eventBus.PublishAsync(
                new ConvergenceCheckTriggered(_state.Convergence.CurrentIteration, true, result.FailureSignature), ct);

            if (noProgress >= 3)
            {
                logger.LogError("无进展检测触发，连续 {N} 次相同签名", noProgress);
                await TransitionStateAsync(task, AgentTaskStatus.Failed, "无进展", ct);
                MoveToFailed(task with { Status = AgentTaskStatus.Failed });
                return;
            }
            UpdateConvergence(noProgress, result.FailureSignature);
        }

        // 超过最大重试次数
        if (newAttempt >= convergence.MaxAttempts)
        {
            logger.LogError("任务超过最大重试次数 {Max}", convergence.MaxAttempts);
            await TransitionStateAsync(task, AgentTaskStatus.Failed, "超过最大重试次数", ct);
            MoveToFailed(task with { Status = AgentTaskStatus.Failed });
            return;
        }

        // 回流重试（Attempt+1）
        var retryTask = task with
        {
            Attempt = newAttempt,
            Status = AgentTaskStatus.Init,
            Tags = new Dictionary<string, string>(task.Tags)
            {
                ["lastFailure"] = result.Summary,
                ["failureSignature"] = result.FailureSignature ?? string.Empty
            }
        };

        // 多次失败自动插入 Reflector
        if (newAttempt == 2)
        {
            await EnqueueAsync(new AgentTask
            {
                Type = "reflect",
                InputRef = task.InputRef,
                ParentTaskId = task.ParentTaskId,
                DependsOn = [task.Id]
            }, ct);
        }

        await EnqueueAsync(retryTask, ct);
    }

    private async Task HandleTimeoutAsync(AgentTask task, CancellationToken ct)
    {
        await TransitionStateAsync(task, AgentTaskStatus.TimedOut, "任务超时", ct);
        var retry = task with { Attempt = task.Attempt + 1, Status = AgentTaskStatus.Init };
        if (retry.Attempt < convergence.MaxAttempts)
            await EnqueueAsync(retry, ct);
        else
            MoveToFailed(task with { Status = AgentTaskStatus.Failed });
    }

    private async Task HandleFailureAsync(AgentTask task, string reason, CancellationToken ct)
    {
        await TransitionStateAsync(task, AgentTaskStatus.Failed, reason, ct);
        MoveToFailed(task with { Status = AgentTaskStatus.Failed });
    }

    private bool CheckConvergence(AgentTask task)
    {
        var conv = _state.Convergence;
        if (conv.CurrentIteration >= conv.MaxIterations)
        {
            logger.LogError("达到最大迭代轮次 {Max}", conv.MaxIterations);
            OrchestratorMetrics.ConvergenceIterations.Record(conv.CurrentIteration);
            return false;
        }
        return true;
    }

    private bool AreDependenciesMet(AgentTask task)
    {
        if (task.DependsOn.Count == 0) return true;
        var completedIds = _state.Completed.Select(t => t.Id).ToHashSet();
        return task.DependsOn.All(id => completedIds.Contains(id));
    }

    private async Task EnqueueAsync(AgentTask task, CancellationToken ct)
    {
        await _taskChannel.Writer.WriteAsync(task, ct);
        lock (_stateLock)
        {
            _state = _state with { Queue = [.. _state.Queue, task] };
        }
    }

    private void MoveToCompleted(AgentTask task)
    {
        lock (_stateLock)
        {
            _state = _state with
            {
                Queue = _state.Queue.Where(t => t.Id != task.Id).ToList(),
                Completed = [.. _state.Completed, task],
                Convergence = _state.Convergence with
                {
                    CurrentIteration = _state.Convergence.CurrentIteration + 1
                }
            };
        }
    }

    private void MoveToFailed(AgentTask task)
    {
        lock (_stateLock)
        {
            _state = _state with
            {
                Queue = _state.Queue.Where(t => t.Id != task.Id).ToList(),
                Failed = [.. _state.Failed, task]
            };
        }
    }

    private void UpdateConvergence(int noProgress, string signature)
    {
        lock (_stateLock)
        {
            _state = _state with
            {
                Convergence = _state.Convergence with
                {
                    NoProgressCount = noProgress,
                    LastFailureSignature = signature
                }
            };
        }
    }

    private async Task TransitionStateAsync(AgentTask task, AgentTaskStatus to, string reason, CancellationToken ct)
    {
        try
        {
            StateMachineValidator.Validate(task.Status, to);
            await eventBus.PublishAsync(new StateTransitioned(task.Id, task.Status, to, reason), ct);
        }
        catch (InvalidTransitionException ex)
        {
            OrchestratorMetrics.StateTransitionErrors.Add(1);
            logger.LogError(ex, "非法状态迁移 {From} → {To}", task.Status, to);
        }
    }

    private async Task PublishBudgetExceededAsync(CancellationToken ct)
    {
        var budget = _state.Budget;
        if (budget.IsTokenBudgetExceeded)
            await eventBus.PublishAsync(new BudgetExceeded("tokens", budget.TotalTokensUsed, budget.MaxTokens), ct);
        if (budget.IsCostBudgetExceeded)
            await eventBus.PublishAsync(new BudgetExceeded("cost", budget.TotalCostUsed, budget.MaxCost), ct);
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        OrchestratorState snapshot;
        lock (_stateLock)
        {
            snapshot = _state.IncrementVersion();
            _state = snapshot;
        }
        await stateStore.SaveAsync(snapshot, ct);
    }

    /// <summary>
    /// 获取当前状态快照（用于 status 命令）
    /// </summary>
    public OrchestratorState GetStatus() => _state;
}

/// <summary>
/// 收敛与超时配置
/// </summary>
public record ConvergenceConfig
{
    public int MaxIterations { get; init; } = 20;
    public int MaxAttempts { get; init; } = 3;
    public int CliTimeoutSeconds { get; init; } = 120;
    public int TaskTimeoutSeconds { get; init; } = 600;
    public double MaxCost { get; init; } = 10.0;
    public int MaxTokens { get; init; } = 1_000_000;
    public double MinPassRate { get; init; } = 0.8;
}