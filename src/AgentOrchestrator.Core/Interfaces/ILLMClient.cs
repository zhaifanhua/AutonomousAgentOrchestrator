using AgentOrchestrator.Core.Domain;

namespace AgentOrchestrator.Core.Interfaces;

/// <summary>
/// LLM 客户端抽象：屏蔽 Claude CLI / Codex CLI / OpenAI API 差异
/// </summary>
public interface ILLMClient
{
    string ProviderName { get; }
    IReadOnlySet<string> SupportedModels { get; }

    /// <summary>
    /// 同步调用（带超时和降级重试）
    /// </summary>
    Task<LLMResponse> ExecuteAsync(InvocationSpec spec, CancellationToken ct);

    /// <summary>
    /// 流式调用（长输出场景，避免超时）
    /// </summary>
    IAsyncEnumerable<LLMToken> StreamAsync(InvocationSpec spec, CancellationToken ct);

    /// <summary>
    /// 查询模型能力（上下文窗口、结构化输出支持等）
    /// </summary>
    Task<ModelCapability> GetCapabilityAsync(string modelId, CancellationToken ct);
}

/// <summary>
/// 语义缓存：拦截相似 prompt，基于嵌入相似度命中缓存
/// </summary>
public interface ISemanticCache
{
    /// <summary>
    /// 尝试从缓存获取（相似度阈值默认 0.95）
    /// </summary>
    Task<LLMResponse?> TryGetAsync(InvocationSpec spec, float threshold, CancellationToken ct);

    Task SetAsync(InvocationSpec spec, LLMResponse response, CancellationToken ct);
}

/// <summary>
/// 嵌入向量服务
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// 生成文本的嵌入向量
    /// </summary>
    Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct);

    /// <summary>
    /// 计算两向量余弦相似度
    /// </summary>
    float CosineSimilarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b);
}