using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using AgentOrchestrator.Infrastructure.LLMClients;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentOrchestrator.Tests;

public class ClaudeCliClientTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldPassPromptThroughStdin()
    {
        var sandbox = new CaptureSandbox();
        var client = new ClaudeCliClient(
            "claude",
            sandbox,
            NullLogger<ClaudeCliClient>.Instance);

        var spec = new InvocationSpec(
            ModelId: "sonnet",
            SystemPrompt: "系统提示\n第二行",
            UserPrompt: "用户需求：只输出 JSON");

        await client.ExecuteAsync(spec, CancellationToken.None);

        var invocation = Assert.Single(sandbox.Invocations);
        Assert.Contains("-p", invocation.Arguments);
        Assert.Contains("--model", invocation.Arguments);
        Assert.Contains("--no-session-persistence", invocation.Arguments);
        Assert.Contains("--output-format", invocation.Arguments);
        Assert.DoesNotContain("--bare", invocation.Arguments);
        Assert.DoesNotContain("--system-prompt", invocation.Arguments);
        Assert.DoesNotContain(invocation.Arguments, arg => arg.Contains("系统提示", StringComparison.Ordinal));
        Assert.NotNull(invocation.StdInput);
        Assert.Contains("<system>", invocation.StdInput);
        Assert.Contains("系统提示", invocation.StdInput);
        Assert.Contains("<user>", invocation.StdInput);
        Assert.Contains("用户需求", invocation.StdInput);
    }

    private sealed class CaptureSandbox : IToolSandbox
    {
        public List<ToolInvocation> Invocations { get; } = [];

        public Task<ToolResult> ExecuteAsync(ToolInvocation inv, CancellationToken ct)
        {
            Invocations.Add(inv);
            return Task.FromResult(new ToolResult(
                0,
                "{\"ok\":true}",
                string.Empty,
                TimeSpan.FromMilliseconds(1),
                0));
        }
    }
}
