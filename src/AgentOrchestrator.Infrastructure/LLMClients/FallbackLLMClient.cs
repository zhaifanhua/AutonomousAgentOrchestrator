using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace AgentOrchestrator.Infrastructure.LLMClients;

/// <summary>
/// 具备自动降级的 LLM 客户端装饰器。
/// 策略：
/// 1. 优先选择声明支持目标 ModelId 的客户端。
/// 2. 若无客户端支持该 ModelId（如路由给 gpt-4o-mini 但只配置了 Claude CLI），
///    则降级到列表中第一个可用客户端并替换 ModelId，确保始终有兜底。
/// 3. 同一 ModelId 的客户端失败后继续尝试下一个（原有行为）。
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
        var attempted = new HashSet<string>(StringComparer.Ordinal);

        // 先尝试精确匹配目标 ModelId 的客户端
        foreach (var client in clients.Where(c => c.SupportedModels.Contains(spec.ModelId)))
        {
            var key = BuildAttemptKey(client, spec.ModelId);
            if (!attempted.Add(key))
            {
                continue;
            }

            var (response, error) = await TryExecuteAsync(client, spec, ct);
            if (response != null)
            {
                return response;
            }

            lastEx = error;
        }

        // 精确匹配失败或不存在时，选择尚未尝试过的其他 provider/model 降级。
        foreach (var client in clients.Where(c => c.SupportedModels.Count > 0))
        {
            var fallbackModel = client.SupportedModels.Contains(spec.ModelId)
                ? spec.ModelId
                : client.SupportedModels.First();
            var key = BuildAttemptKey(client, fallbackModel);
            if (!attempted.Add(key))
            {
                continue;
            }

            logger.LogWarning(
                "LLM 模型 {Model} 降级到 {Provider}/{FallbackModel}",
                spec.ModelId, client.ProviderName, fallbackModel);

            var (response, error) = await TryExecuteAsync(client, spec with { ModelId = fallbackModel }, ct);
            if (response != null)
            {
                return response;
            }

            lastEx = error;
        }

        throw new LLMClientException($"所有 LLM 客户端均失败，模型={spec.ModelId}", lastEx);

        async Task<(LLMResponse? Response, Exception? Error)> TryExecuteAsync(
            ILLMClient client,
            InvocationSpec callSpec,
            CancellationToken cancellationToken)
        {
            try
            {
                return (await client.ExecuteAsync(callSpec, cancellationToken), null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "LLM 客户端 {Provider} 失败，尝试下一个（模型={Model}, 错误={Error}）",
                    client.ProviderName, callSpec.ModelId, ex.Message);
                return (null, ex);
            }
        }
    }

    public async IAsyncEnumerable<LLMToken> StreamAsync(
        InvocationSpec spec,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 精确匹配
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

        // 降级
        var fallback = clients.FirstOrDefault(c => c.SupportedModels.Count > 0);
        if (fallback != null)
        {
            var fallbackModel = fallback.SupportedModels.Contains(spec.ModelId)
                ? spec.ModelId
                : fallback.SupportedModels.First();

            await foreach (var token in fallback.StreamAsync(spec with { ModelId = fallbackModel }, ct))
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
            ?? clients.FirstOrDefault()
            ?? throw new LLMClientException($"无任何 LLM 客户端可用");
        return client.GetCapabilityAsync(modelId, ct);
    }

    private static string BuildAttemptKey(ILLMClient client, string modelId) =>
        $"{client.ProviderName}\u001F{modelId}";
}
