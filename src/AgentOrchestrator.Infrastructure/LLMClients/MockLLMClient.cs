using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AgentOrchestrator.Infrastructure.LLMClients;

/// <summary>
/// Mock LLM 客户端：用于测试和 dry-run 模式。
/// 根据任务类型返回预设的合法 JSON 响应。
/// </summary>
public class MockLLMClient : ILLMClient
{
    public string ProviderName => "mock";

    public IReadOnlySet<string> SupportedModels => new HashSet<string>
    {
        "mock-model", "mock-fast", "mock-smart"
    };

    public Task<LLMResponse> ExecuteAsync(InvocationSpec spec, CancellationToken ct)
    {
        var content = GenerateMockResponse(spec);
        var usage = new TokenUsage(100, 200, spec.ModelId, 0.001);
        return Task.FromResult(new LLMResponse(content, usage, TimeSpan.FromMilliseconds(50), spec.ModelId, "stop"));
    }

    public async IAsyncEnumerable<LLMToken> StreamAsync(
        InvocationSpec spec,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var response = await ExecuteAsync(spec, ct);
        foreach (var chunk in ChunkString(response.Content, 30))
        {
            yield return new LLMToken(chunk);
            await Task.Delay(10, ct);
        }
        yield return new LLMToken(string.Empty, IsLast: true);
    }

    public Task<ModelCapability> GetCapabilityAsync(string modelId, CancellationToken ct) =>
        Task.FromResult(new ModelCapability(modelId, 100_000, true, true, 0, 0));

    private static string GenerateMockResponse(InvocationSpec spec)
    {
        // 根据 system prompt 中的关键词判断任务类型，返回对应 JSON
        var lower = spec.SystemPrompt.ToLowerInvariant() + spec.UserPrompt.ToLowerInvariant();

        if (lower.Contains("plan"))
        {
            return JsonSerializer.Serialize(BuildPlannerResponse());
        }

        if (lower.Contains("dev") || lower.Contains("implement"))
        {
            return JsonSerializer.Serialize(BuildDeveloperResponse());
        }

        if (lower.Contains("test"))
        {
            return JsonSerializer.Serialize(BuildTesterResponse());
        }

        if (lower.Contains("critic") || lower.Contains("review"))
        {
            return JsonSerializer.Serialize(BuildCriticResponse());
        }

        if (lower.Contains("reflect"))
        {
            return JsonSerializer.Serialize(BuildReflectorResponse());
        }

        return JsonSerializer.Serialize(new { success = true, notes = "mock response" });
    }

    private static object BuildPlannerResponse() => new
    {
        modules = new[] { new { name = "Core", responsibility = "核心逻辑", dependencies = Array.Empty<string>() } },
        file_plan = new[] { new { path = "src/Core.cs", action = "create", rationale = "Mock 计划" } },
        steps = new[] { new { order = 1, description = "实现核心逻辑", agent = "dev" } },
        risks = new[] { new { description = "无风险", mitigation = "N/A", severity = "low" } },
        definition_of_done = new[] { "所有测试通过" },
        notes = "mock planner response"
    };

    private static object BuildDeveloperResponse() => new
    {
        edits = new[] { new { path = "src/Mock.cs", patch_or_full_file = "// mock code\npublic class Mock {}", rationale = "Mock 实现" } },
        notes = "mock developer response"
    };

    private static object BuildTesterResponse() => new
    {
        cases = new[] { new { name = "MockTest", input = "{}", expected = "true" } },
        executed_commands = new[] { "dotnet test" },
        pass = true,
        failure_signature = "",
        bugs = Array.Empty<object>(),
        notes = "mock tester response"
    };

    private static object BuildCriticResponse() => new
    {
        issues = Array.Empty<object>(),
        severity = "low",
        suggestions = new[] { "代码结构清晰" },
        notes = "mock critic response"
    };

    private static object BuildReflectorResponse() => new
    {
        strategy_adjustments = new[] { "保持当前策略" },
        lessons = new[] { "Mock 学到的教训" },
        notes = "mock reflector response"
    };

    private static IEnumerable<string> ChunkString(string s, int size)
    {
        for (var i = 0; i < s.Length; i += size)
        {
            yield return s.Substring(i, Math.Min(size, s.Length - i));
        }
    }
}