using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Infrastructure.LLMClients;
using System.Text.Json;

namespace AgentOrchestrator.Tests;

public class MockLLMClientTests
{
    private readonly MockLLMClient _client = new();

    [Fact]
    public async Task Execute_PlanRequest_ShouldReturnValidJson()
    {
        var spec = new InvocationSpec("mock-model", "You are a planner", "plan this feature", 1000);
        var response = await _client.ExecuteAsync(spec, CancellationToken.None);

        Assert.NotEmpty(response.Content);
        var doc = JsonDocument.Parse(response.Content);
        Assert.True(doc.RootElement.TryGetProperty("modules", out _) ||
                    doc.RootElement.TryGetProperty("steps", out _));
    }

    [Fact]
    public async Task Execute_ShouldReturnUsage()
    {
        var spec = new InvocationSpec("mock-model", "system", "user", 1000);
        var response = await _client.ExecuteAsync(spec, CancellationToken.None);

        Assert.Equal("mock-model", response.Usage.ModelId);
        Assert.True(response.Usage.Prompt > 0);
        Assert.True(response.Usage.Completion > 0);
    }

    [Fact]
    public async Task Stream_ShouldYieldTokens()
    {
        var spec = new InvocationSpec("mock-model", "system", "user", 100);
        var tokens = new List<LLMToken>();

        await foreach (var token in _client.StreamAsync(spec, CancellationToken.None))
            tokens.Add(token);

        Assert.NotEmpty(tokens);
        Assert.True(tokens.Last().IsLast);
    }
}