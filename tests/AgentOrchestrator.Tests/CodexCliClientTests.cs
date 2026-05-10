using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using AgentOrchestrator.Infrastructure.LLMClients;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentOrchestrator.Tests;

public class CodexCliClientTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldPassPromptThroughStdin()
    {
        var sandbox = new CaptureSandbox();
        var client = new CodexCliClient(
            "codex",
            sandbox,
            NullLogger<CodexCliClient>.Instance);

        var spec = new InvocationSpec(
            ModelId: "codex-mini-latest",
            SystemPrompt: "系统提示\n第二行",
            UserPrompt: "用户需求：只输出 JSON");

        await client.ExecuteAsync(spec, CancellationToken.None);

        var invocation = Assert.Single(sandbox.Invocations);
        Assert.Contains("exec", invocation.Arguments);
        Assert.Contains("-m", invocation.Arguments);
        Assert.Contains("codex-mini-latest", invocation.Arguments);
        Assert.Contains("--ephemeral", invocation.Arguments);
        Assert.Contains("-s", invocation.Arguments);
        Assert.Contains("read-only", invocation.Arguments);
        Assert.Contains("--ask-for-approval", invocation.Arguments);
        Assert.Contains("never", invocation.Arguments);
        Assert.True(
            Array.IndexOf(invocation.Arguments, "--ask-for-approval") <
            Array.IndexOf(invocation.Arguments, "exec"));
        Assert.Contains("--color", invocation.Arguments);
        Assert.Contains("--output-last-message", invocation.Arguments);
        Assert.Equal("-", invocation.Arguments[^1]);
        Assert.True(
            Array.IndexOf(invocation.Arguments, "--output-last-message") <
            Array.LastIndexOf(invocation.Arguments, "-"));
        Assert.DoesNotContain(invocation.Arguments, arg => arg.Contains("系统提示", StringComparison.Ordinal));
        Assert.DoesNotContain(invocation.Arguments, arg => arg.Contains("用户需求", StringComparison.Ordinal));
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
