using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentOrchestrator.Infrastructure.Memory;

/// <summary>
/// 本地轻量嵌入服务。
/// 使用 TF-IDF 风格的词袋向量作为语义嵌入的近似实现（零外部依赖）。
/// 生产环境可替换为调用 sentence-transformers 或 OpenAI Embeddings API。
/// </summary>
public class LocalEmbeddingService : IEmbeddingService
{
    private const int VectorSize = 256;

    // logger 保留供 DI 注入与未来扩展（当前嵌入实现无日志输出）
    public LocalEmbeddingService(ILogger<LocalEmbeddingService> _)
    {
    }

    public Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct)
    {
        var vector = ComputeHashEmbedding(text);
        return Task.FromResult(new ReadOnlyMemory<float>(vector));
    }

    public float CosineSimilarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var spanA = a.Span;
        var spanB = b.Span;
        if (spanA.Length != spanB.Length)
        {
            return 0f;
        }

        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < spanA.Length; i++)
        {
            dot += spanA[i] * spanB[i];
            normA += spanA[i] * spanA[i];
            normB += spanB[i] * spanB[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom < 1e-8f ? 0f : dot / denom;
    }

    /// <summary>
    /// 基于字符 n-gram 哈希生成定长向量（MinHash 近似）。
    /// 词序不敏感，适合短文本语义相似度估算。
    /// </summary>
    private static float[] ComputeHashEmbedding(string text)
    {
        var vector = new float[VectorSize];
        var lower = text.ToLowerInvariant();

        // 2-gram 和 3-gram 特征哈希
        for (var n = 2; n <= 3; n++)
        {
            for (var i = 0; i <= lower.Length - n; i++)
            {
                var ngram = lower.AsSpan(i, n);
                var hash = ComputeSimpleHash(ngram);
                var idx = Math.Abs(hash % VectorSize);
                vector[idx] += 1.0f;
            }
        }

        // L2 归一化
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 1e-8f)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }

    private static int ComputeSimpleHash(ReadOnlySpan<char> s)
    {
        var hash = 17;
        foreach (var c in s)
        {
            hash = (hash * 31) + c;
        }

        return hash;
    }
}
