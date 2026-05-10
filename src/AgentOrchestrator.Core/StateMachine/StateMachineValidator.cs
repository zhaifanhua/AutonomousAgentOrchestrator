namespace AgentOrchestrator.Core.StateMachine;

/// <summary>
/// 非法状态迁移异常
/// </summary>
public class InvalidTransitionException(AgentTaskStatus from, AgentTaskStatus to)
    : Exception($"非法状态迁移: {from} → {to}");

/// <summary>
/// 状态机校验器，定义并验证合法的状态迁移路径
/// </summary>
public static class StateMachineValidator
{
    // 合法的迁移路径表
    private static readonly Dictionary<AgentTaskStatus, HashSet<AgentTaskStatus>> AllowedTransitions = new()
    {
        [AgentTaskStatus.Init] = [AgentTaskStatus.Plan, AgentTaskStatus.Cancelled],
        [AgentTaskStatus.Plan] = [AgentTaskStatus.Dev, AgentTaskStatus.Failed, AgentTaskStatus.PausedForApproval, AgentTaskStatus.Cancelled],
        [AgentTaskStatus.Dev] = [AgentTaskStatus.Test, AgentTaskStatus.Plan, AgentTaskStatus.Failed, AgentTaskStatus.PausedForApproval, AgentTaskStatus.TimedOut, AgentTaskStatus.Cancelled],
        [AgentTaskStatus.Test] = [AgentTaskStatus.Dev, AgentTaskStatus.Done, AgentTaskStatus.Failed, AgentTaskStatus.PausedForApproval, AgentTaskStatus.TimedOut, AgentTaskStatus.Cancelled],
        [AgentTaskStatus.PausedForApproval] = [AgentTaskStatus.Dev, AgentTaskStatus.Plan, AgentTaskStatus.Failed, AgentTaskStatus.Cancelled],
        [AgentTaskStatus.TimedOut] = [AgentTaskStatus.Dev, AgentTaskStatus.Failed, AgentTaskStatus.Cancelled],
        [AgentTaskStatus.Done] = [],
        [AgentTaskStatus.Failed] = [],
        [AgentTaskStatus.Cancelled] = [],
    };

    /// <summary>
    /// 校验迁移是否合法，非法则抛出异常
    /// </summary>
    public static void Validate(AgentTaskStatus from, AgentTaskStatus to)
    {
        if (!AllowedTransitions.TryGetValue(from, out var allowed) || !allowed.Contains(to))
        {
            throw new InvalidTransitionException(from, to);
        }
    }

    /// <summary>
    /// 安全检测迁移是否合法（不抛出异常）
    /// </summary>
    public static bool IsValid(AgentTaskStatus from, AgentTaskStatus to) =>
        AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>
    /// 终止状态不可再迁移
    /// </summary>
    public static bool IsTerminal(AgentTaskStatus status) =>
        status is AgentTaskStatus.Done or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled;
}