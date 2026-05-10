namespace AgentOrchestrator.Core.StateMachine;

/// <summary>
/// 任务生命周期状态枚举
/// </summary>
public enum AgentTaskStatus
{
    /// <summary>
    /// 初始创建
    /// </summary>
    Init,

    /// <summary>
    /// 规划阶段
    /// </summary>
    Plan,

    /// <summary>
    /// 开发阶段
    /// </summary>
    Dev,

    /// <summary>
    /// 测试阶段
    /// </summary>
    Test,

    /// <summary>
    /// 成功完成
    /// </summary>
    Done,

    /// <summary>
    /// 最终失败
    /// </summary>
    Failed,

    /// <summary>
    /// 等待人工审批
    /// </summary>
    PausedForApproval,

    /// <summary>
    /// 单次任务超时
    /// </summary>
    TimedOut,

    /// <summary>
    /// 已取消
    /// </summary>
    Cancelled
}