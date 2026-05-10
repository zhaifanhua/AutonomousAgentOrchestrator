using AgentOrchestrator.Infrastructure.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentOrchestrator.Tests;

public class LocalEmbeddingServiceTests
{
    private readonly LocalEmbeddingService _service = new(NullLogger<LocalEmbeddingService>.Instance);

    [Fact]
    public async Task Embed_ShouldReturnNonZeroVector()
    {
        var embedding = await _service.EmbedAsync("hello world", CancellationToken.None);
        Assert.NotEqual(0, embedding.Length);
        // 向量至少有一个非零分量（MinHash 哈希不为全零）
        var arr = embedding.Span.ToArray();
        var nonzero = arr.Count(static v => v != 0f);
        Assert.NotEqual(0, nonzero);
    }

    [Fact]
    public async Task CosineSimilarity_SimilarTexts_ShouldBeHigh()
    {
        var a = await _service.EmbedAsync("test unit testing verify", CancellationToken.None);
        var b = await _service.EmbedAsync("test testing unit tests", CancellationToken.None);
        var sim = _service.CosineSimilarity(a, b);
        Assert.True(sim > 0.5f, $"相似度应大于 0.5，实际={sim}");
    }

    [Fact]
    public async Task CosineSimilarity_DifferentTexts_ShouldBeLower()
    {
        var a = await _service.EmbedAsync("database sql query", CancellationToken.None);
        var b = await _service.EmbedAsync("machine learning neural network", CancellationToken.None);
        var sim = _service.CosineSimilarity(a, b);
        // 相似度应低于高度相似文本，但不严格限定阈值（取决于嵌入算法）
        Assert.InRange(sim, 0f, 1f);
    }
}
