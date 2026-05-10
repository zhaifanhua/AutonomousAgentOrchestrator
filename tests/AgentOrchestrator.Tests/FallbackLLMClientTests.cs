using System.Runtime.CompilerServices;
using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using AgentOrchestrator.Infrastructure.LLMClients;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentOrchestrator.Tests;

public class FallbackLLMClientTests
{
    [Fact]
    public async Task Execute_WhenExactClientIsCanceled_ShouldNotFallback()
    {
        var exact = new FakeLLMClient(
            "exact",
            new HashSet<string> { "target-model" },
            _ => throw new TaskCanceledException("timeout"));
        var fallback = new FakeLLMClient(
            "fallback",
            new HashSet<string> { "fallback-model" },
            spec => SuccessfulResponse(spec));
        var client = CreateClient(exact, fallback);

        var spec = new InvocationSpec("target-model", "system", "user");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ExecuteAsync(spec, CancellationToken.None));

        Assert.Equal(1, exact.ExecuteCalls);
        Assert.Equal(0, fallback.ExecuteCalls);
    }

    [Fact]
    public async Task Execute_WhenExactClientFails_ShouldUseDifferentFallbackModelOnce()
    {
        var exact = new FakeLLMClient(
            "claude-cli",
            new HashSet<string> { "claude-sonnet-4-5" },
            _ => throw new LLMClientException("cli failed"));
        var fallback = new FakeLLMClient(
            "mock",
            new HashSet<string> { "mock-model" },
            spec => SuccessfulResponse(spec));
        var client = CreateClient(exact, fallback);

        var spec = new InvocationSpec("claude-sonnet-4-5", "system", "user");

        var response = await client.ExecuteAsync(spec, CancellationToken.None);

        Assert.Equal("mock-model", response.ModelId);
        Assert.Equal(1, exact.ExecuteCalls);
        Assert.Equal(1, fallback.ExecuteCalls);
    }

    private static FallbackLLMClient CreateClient(params ILLMClient[] clients) =>
        new(clients, NullLogger<FallbackLLMClient>.Instance);

    private static LLMResponse SuccessfulResponse(InvocationSpec spec) =>
        new("ok", new TokenUsage(1, 1, spec.ModelId, 0), TimeSpan.Zero, spec.ModelId, "stop");

    private sealed class FakeLLMClient(
        string providerName,
        IReadOnlySet<string> supportedModels,
        Func<InvocationSpec, LLMResponse> execute) : ILLMClient
    {
        public int ExecuteCalls { get; private set; }

        public string ProviderName => providerName;

        public IReadOnlySet<string> SupportedModels => supportedModels;

        public Task<LLMResponse> ExecuteAsync(InvocationSpec spec, CancellationToken ct)
        {
            ExecuteCalls++;
            return Task.FromResult(execute(spec));
        }

        public async IAsyncEnumerable<LLMToken> StreamAsync(
            InvocationSpec spec,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var response = await ExecuteAsync(spec, ct);
            yield return new LLMToken(response.Content, IsLast: true);
        }

        public Task<ModelCapability> GetCapabilityAsync(string modelId, CancellationToken ct) =>
            Task.FromResult(new ModelCapability(modelId, 1, true, false, 0, 0));
    }
}
