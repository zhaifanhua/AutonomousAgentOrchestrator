using AgentOrchestrator.Core.StateMachine;

namespace AgentOrchestrator.Tests;

public class StateMachineTests
{
    [Theory]
    [InlineData(AgentTaskStatus.Init, AgentTaskStatus.Plan)]
    [InlineData(AgentTaskStatus.Plan, AgentTaskStatus.Dev)]
    [InlineData(AgentTaskStatus.Dev, AgentTaskStatus.Test)]
    [InlineData(AgentTaskStatus.Test, AgentTaskStatus.Done)]
    [InlineData(AgentTaskStatus.Test, AgentTaskStatus.Dev)]  // 测试失败回流
    public void ValidTransitions_ShouldNotThrow(AgentTaskStatus from, AgentTaskStatus to)
    {
        var ex = Record.Exception(() => StateMachineValidator.Validate(from, to));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(AgentTaskStatus.Done, AgentTaskStatus.Dev)]      // 终止状态不可迁移
    [InlineData(AgentTaskStatus.Failed, AgentTaskStatus.Plan)]   // 终止状态不可迁移
    [InlineData(AgentTaskStatus.Init, AgentTaskStatus.Test)]     // 跳过 Plan/Dev
    public void InvalidTransitions_ShouldThrow(AgentTaskStatus from, AgentTaskStatus to)
    {
        Assert.Throws<InvalidTransitionException>(() =>
            StateMachineValidator.Validate(from, to));
    }

    [Theory]
    [InlineData(AgentTaskStatus.Done)]
    [InlineData(AgentTaskStatus.Failed)]
    [InlineData(AgentTaskStatus.Cancelled)]
    public void TerminalStates_ShouldBeDetected(AgentTaskStatus status)
    {
        Assert.True(StateMachineValidator.IsTerminal(status));
    }
}
