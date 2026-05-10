using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace AgentOrchestrator.Infrastructure.LLMClients;

/// <summary>
/// 具备自动降级的 LLM 客户端装饰器。
/// 主模型失败后按 fallback 列表顺序重试。
/// </summary>
public class FallbackLLMClient(
    IReadOnlyList<ILLMClient> clients,
    ILogger<FallbackLLMClient> logger) : ILLMClient
{
    public string ProviderName => "fallback-composite";

    public IReadOnlySet<string> SupportedModels =>
        clients.SelectMany(c => c.SupportedModels).ToHashSet();

    public async Task<LLMResponse> ExecuteAsync(InvocationSpec spec, CancellationToken ct)
    {
        var lastEx = (Exception?)null;
        foreach (var client in clients)
        {
            if (!client.SupportedModels.Contains(spec.ModelId))
            {
                continue;
            }

            try
            {
                return await client.ExecuteAsync(spec, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "LLM 客户端 {Provider} 失败，尝试下一个", client.ProviderName);
                lastEx = ex;
            }
        }
        throw new LLMClientException($"所有 LLM 客户端均失败，模型={spec.ModelId}", lastEx);
    }

    public async IAsyncEnumerable<LLMToken> StreamAsync(
        InvocationSpec spec,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var client in clients)
        {
            if (!client.SupportedModels.Contains(spec.ModelId))
            {
                continue;
            }

            await foreach (var token in client.StreamAsync(spec, ct))
            {
                yield return token;
            }

            yield break;
        }
        throw new LLMClientException($"无可用客户端支持模型={spec.ModelId}");
    }

    public Task<ModelCapability> GetCapabilityAsync(string modelId, CancellationToken ct)
    {
        var client = clients.FirstOrDefault(c => c.SupportedModels.Contains(modelId))
            ?? throw new LLMClientException($"无客户端支持模型={modelId}");
        return client.GetCapabilityAsync(modelId, ct);
    }
}