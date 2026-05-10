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

    // 追踪所有 fire-and-forget 执行任务，等待其完成后再退出
    private readonly List<Task> _runningTasks = [];

    private readonly Lock _runningTasksLock = new();

    private readonly Lock _stateLock = new();
    private OrchestratorState _state = new();
    private int _activeTaskCount = 0;

    /// <summary>
    /// 启动编排器。
    /// forceNew=true（run 命令）：忽略已有 state.json，全新开始。
    /// forceNew=false（resume 命令）：从 state.json 恢复进度。
    /// </summary>
    public async Task RunAsync(string requirementRef, CancellationToken ct, bool forceNew = false)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("编排器启动，需求文件: {Ref}, 全新启动: {ForceNew}",
                requirementRef, forceNew);
        }

        var freshState = new OrchestratorState
        {
            Project = new ProjectContext
            {
                WorkspacePath = workspace.RootPath,
                RequirementSummary = requirementRef,
                PathsAllowlist = ["src/", "tests/", "plans/", "reports/"],
            },
            Budget = new BudgetState
            {
                MaxTokens = convergence.MaxTokens,
                MaxCost = convergence.MaxCost,
                MaxTokensPerTask = convergence.MaxTokensPerTask,
            },
            Convergence = new ConvergenceState
            {
                MaxIterations = convergence.MaxIterations,
            },
        };

        // forceNew: 不加载旧状态；resume: 加载已有状态
        _state = forceNew
            ? freshState
            : (await stateStore.LoadAsync(ct) ?? freshState);

        OrchestratorMetrics.RegisterActiveTasksGauge(() => _activeTaskCount);

        if (forceNew || (_state.Queue.Count == 0 && _state.Completed.Count == 0 && _state.Failed.Count == 0))
        {
            // 全新运行：入队初始 plan 任务
            await EnqueueAsync(new AgentTask
            {
                Type = "plan",
                InputRef = requirementRef,
                Tags = new Dictionary<string, string> { ["initial"] = "true" }
            }, ct);
        }
        else if (_state.Queue.Count > 0)
        {
            // resume：仅将待执行任务写入通道（状态已加载，不重复添加）
            foreach (var task in _state.Queue)
            {
                await _taskChannel.Writer.WriteAsync(task, ct);
            }
        }
        else
        {
            // 队列已空（上次运行已完成或全部失败）→ 直接报告，不阻塞
            logger.LogInformation("上次运行已结束，完成={Done} 失败={Failed}，无任务需要恢复",
                _state.Completed.Count, _state.Failed.Count);
            _taskChannel.Writer.TryComplete();
        }

        await ProcessLoopAsync(ct);

        // 等待所有后台任务执行完毕再退出
        Task[] pending;
        lock (_runningTasksLock)
        {
            pending = [.. _runningTasks];
        }

        if (pending.Length > 0)
        {
            await Task.WhenAll(pending);
        }
    }

    /// <summary>
    /// 获取当前状态快照（用于 status 命令）
    /// </summary>
    public OrchestratorState GetStatus() => _state;

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        // 记录依赖未满足的任务跳过次数，超过阈值则强制中止（防止忙等死锁）
        var skipCounts = new Dictionary<Guid, int>();

        await foreach (var task in _taskChannel.Reader.ReadAllAsync(ct))
        {
            // 收敛检测
            if (!CheckConvergence())
            {
                break;
            }

            // 预算检查
            if (_state.Budget.IsExceeded)
            {
                logger.LogWarning("预算超限，暂停编排");
                await PublishBudgetExceededAsync(ct);
                break;
            }

            // 依赖检查：未满足则放回队尾，跳过次数超过阈值报错
            if (!AreDependenciesMet(task))
            {
                skipCounts.TryGetValue(task.Id, out var skips);
                skips++;
                if (skips > 20)
                {
                    logger.LogError("任务 {TaskId} 依赖无法满足，强制失败（死锁防护）", task.Id);
                    await HandleFailureAsync(task, "依赖超时未满足", ct);
                    await SaveStateAsync(ct);
                    skipCounts.Remove(task.Id);
                    continue;
                }

                skipCounts[task.Id] = skips;
                // 短暂等待，避免忙等消耗 CPU
                await Task.Delay(100, ct);
                await _taskChannel.Writer.WriteAsync(task with { }, ct);
                continue;
            }

            skipCounts.Remove(task.Id);

            // 启动任务，跟踪 Task 对象，避免未观察异常
            var t = ExecuteTaskAsync(task, ct);
            lock (_runningTasksLock)
            {
                _runningTasks.Add(t);
                // 完成后从列表移除，避免无限增长
                _ = t.ContinueWith(_ =>
                {
                    lock (_runningTasksLock) { _runningTasks.Remove(t); }
                }, TaskScheduler.Default);
            }
        }

        _taskChannel.Writer.TryComplete();
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("编排器结束，完成={Done} 失败={Failed}",
                _state.Completed.Count, _state.Failed.Count);
        }
    }

    private async Task ExecuteTaskAsync(AgentTask task, CancellationToken ct)
    {
        await _parallelSem.WaitAsync(ct);
        Interlocked.Increment(ref _activeTaskCount);
        var currentTask = task;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var activity = OrchestratorMetrics.ActivitySource.StartActivity("Task.Execute");
        activity?.SetTag("taskId", task.Id.ToString());
        activity?.SetTag("taskType", task.Type);

        try
        {
            currentTask = await TransitionStateAsync(
                currentTask,
                GetRunningStatus(currentTask.Type),
                "开始执行",
                ct);
            await eventBus.PublishAsync(new TaskDequeued(currentTask.Id, currentTask.Type, DateTime.UtcNow), ct);

            // 路由决策
            var route = await router.RouteAsync(currentTask, _state.Project, ct);
            await eventBus.PublishAsync(
                new TaskRouted(currentTask.Id, route.AgentType, route.ModelId, route.Confidence), ct);

            // 查找 Agent
            var agent = agentRegistry.GetAgent(route.AgentType)
                ?? throw new InvalidOperationException($"未找到 Agent: {route.AgentType}");

            // 注入模型选择到 Tags
            var enrichedTask = currentTask with
            {
                Tags = new Dictionary<string, string>(currentTask.Tags) { ["modelId"] = route.ModelId }
            };
            currentTask = enrichedTask;

            var agentCtx = new AgentContext(currentTask, _state.Project, workspace, memory, eventBus,
                logger, new CancellationTokenSource(TimeSpan.FromSeconds(convergence.CliTimeoutSeconds)));

            await eventBus.PublishAsync(
                new AgentExecutionStarted(currentTask.Id, route.AgentType, DateTime.UtcNow), ct);

            // 单任务超时 Token
            using var taskTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            taskTimeout.CancelAfter(TimeSpan.FromSeconds(convergence.TaskTimeoutSeconds));

            var result = await agent.ExecuteAsync(agentCtx, taskTimeout.Token);
            sw.Stop();

            await eventBus.PublishAsync(
                new AgentExecutionCompleted(currentTask.Id, result, sw.Elapsed), ct);

            OrchestratorMetrics.TaskDurationSeconds.Record(sw.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("agentType", route.AgentType));

            // 将本次任务的 Token/Cost 消耗累加到预算状态
            AccumulateBudget(result.TokenUsage);

            await HandleResultAsync(currentTask, result, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("任务超时: {TaskId}", currentTask.Id);
            await HandleTimeoutAsync(currentTask, ct);
            await SaveStateAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "任务执行异常: {TaskId}", currentTask.Id);
            await HandleFailureAsync(currentTask, ex.Message, ct);
            await SaveStateAsync(ct);
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
            var completedTask = await TransitionStateAsync(task, AgentTaskStatus.Done, "执行成功", ct);
            MoveToCompleted(completedTask);

            // 将后续任务入队
            foreach (var next in result.NextTasks)
            {
                await EnqueueAsync(next, ct);
            }

            // 无后续任务且通道空闲 → 关闭通道
            if (result.NextTasks.Count == 0 && _taskChannel.Reader.Count == 0 && _activeTaskCount <= 1)
            {
                _taskChannel.Writer.TryComplete();
            }
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

        // 无进展检测：连续相同签名 ≥ 3 次 → 彻底失败
        if (result.FailureSignature != null &&
            result.FailureSignature == _state.Convergence.LastFailureSignature)
        {
            var noProgress = _state.Convergence.NoProgressCount + 1;
            await eventBus.PublishAsync(
                new ConvergenceCheckTriggered(_state.Convergence.CurrentIteration, true, result.FailureSignature), ct);

            if (noProgress >= 3)
            {
                logger.LogError("无进展检测触发，连续 {N} 次相同签名", noProgress);
                var failedTask = await TransitionStateAsync(task, AgentTaskStatus.Failed, "无进展", ct);
                MoveToFailed(failedTask);
                return;
            }

            UpdateConvergence(noProgress, result.FailureSignature);
        }
        else if (result.FailureSignature != null)
        {
            // 新签名，重置无进展计数
            UpdateConvergence(0, result.FailureSignature);
        }

        // 超过最大重试次数
        if (newAttempt >= convergence.MaxAttempts)
        {
            logger.LogError("任务超过最大重试次数 {Max}", convergence.MaxAttempts);
            var failedTask = await TransitionStateAsync(task, AgentTaskStatus.Failed, "超过最大重试次数", ct);
            MoveToFailed(failedTask);
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

        // 第二次失败自动插入 Reflector，帮助元认知分析
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
        var timedOutTask = await TransitionStateAsync(task, AgentTaskStatus.TimedOut, "任务超时", ct);
        var retry = task with { Attempt = task.Attempt + 1, Status = AgentTaskStatus.Init };
        if (retry.Attempt < convergence.MaxAttempts)
        {
            await EnqueueAsync(retry, ct);
        }
        else
        {
            MoveToFailed(timedOutTask with { Status = AgentTaskStatus.Failed });
        }
    }

    private async Task HandleFailureAsync(AgentTask task, string reason, CancellationToken ct)
    {
        var failedTask = await TransitionStateAsync(task, AgentTaskStatus.Failed, reason, ct);
        MoveToFailed(failedTask);
    }

    /// <summary>
    /// 将本次调用的 Token 消耗累加到状态内的预算计数器。
    /// </summary>
    private void AccumulateBudget(TokenUsage usage)
    {
        if (usage.Total == 0 && usage.CostEstimate == 0)
        {
            return;
        }

        lock (_stateLock)
        {
            _state = _state with
            {
                Budget = _state.Budget with
                {
                    TotalTokensUsed = _state.Budget.TotalTokensUsed + usage.Total,
                    TotalCostUsed = _state.Budget.TotalCostUsed + usage.CostEstimate,
                }
            };
        }
    }

    /// <summary>
    /// 检查迭代轮次是否超出上限（从 _state.Convergence 读取，运行时已从 convergence 配置初始化）。
    /// </summary>
    private bool CheckConvergence()
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
        if (task.DependsOn.Count == 0)
        {
            return true;
        }

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
                Queue = [.. _state.Queue.Where(t => t.Id != task.Id)],
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
                Queue = [.. _state.Queue.Where(t => t.Id != task.Id)],
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

    private static AgentTaskStatus GetRunningStatus(string taskType) =>
        taskType.ToLowerInvariant() switch
        {
            "dev" or "implement" => AgentTaskStatus.Dev,
            "test" or "verify" => AgentTaskStatus.Test,
            "gate" => AgentTaskStatus.PausedForApproval,
            _ => AgentTaskStatus.Plan,
        };

    private async Task<AgentTask> TransitionStateAsync(AgentTask task, AgentTaskStatus to, string reason, CancellationToken ct)
    {
        try
        {
            StateMachineValidator.Validate(task.Status, to);
            await eventBus.PublishAsync(new StateTransitioned(task.Id, task.Status, to, reason), ct);
            return task with
            {
                Status = to,
                FinishedAt = StateMachineValidator.IsTerminal(to) ? DateTime.UtcNow : task.FinishedAt,
            };
        }
        catch (InvalidTransitionException ex)
        {
            OrchestratorMetrics.StateTransitionErrors.Add(1);
            logger.LogError(ex, "非法状态迁移 {From} → {To}", task.Status, to);
            return task;
        }
    }

    private async Task PublishBudgetExceededAsync(CancellationToken ct)
    {
        var budget = _state.Budget;
        if (budget.IsTokenBudgetExceeded)
        {
            await eventBus.PublishAsync(new BudgetExceeded("tokens", budget.TotalTokensUsed, budget.MaxTokens), ct);
        }

        if (budget.IsCostBudgetExceeded)
        {
            await eventBus.PublishAsync(new BudgetExceeded("cost", budget.TotalCostUsed, budget.MaxCost), ct);
        }
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
    public int MaxTokensPerTask { get; init; } = 50_000;
    public double MinPassRate { get; init; } = 0.8;
}
