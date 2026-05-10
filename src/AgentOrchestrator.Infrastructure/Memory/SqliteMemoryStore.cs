using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AgentOrchestrator.Infrastructure.Memory;

/// <summary>
/// 基于 SQLite 的语义记忆存储。
/// 向量存储为 JSON 数组（BLOB），检索时在内存中计算余弦相似度。
/// 支持衰减清理、矛盾检测和低置信度过滤。
/// </summary>
public class SqliteMemoryStore(
    string dbPath,
    IEmbeddingService embedding,
    ILogger<SqliteMemoryStore> logger) : IMemoryStore, IDisposable, IAsyncDisposable
{
    private const double ConfidenceThreshold = 0.3;
    private SqliteConnection? _conn;

    /// <summary>防止 Dispose / DisposeAsync 重复释放。</summary>
    private bool _disposed;

    public async Task InitializeAsync(CancellationToken ct)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        await _conn.OpenAsync(ct);
        await CreateSchemaAsync(ct);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("语义记忆 SQLite 已初始化: {Path}", dbPath);
        }
    }

    public async Task StoreAsync(MemoryEntry entry, CancellationToken ct)
    {
        EnsureInitialized();
        var embeddingData = await embedding.EmbedAsync(entry.Content, ct);
        var embeddingJson = JsonSerializer.Serialize(embeddingData.ToArray());

        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO memories
            (id, content, embedding, type, status, created_at, last_accessed_at, access_count,
             decay_factor, confidence, tags, related_task_id)
            VALUES (@id, @content, @emb, @type, @status, @created, @accessed, @count,
                    @decay, @conf, @tags, @related)
            """;
        cmd.Parameters.AddWithValue("@id", entry.Id.ToString());
        cmd.Parameters.AddWithValue("@content", entry.Content);
        cmd.Parameters.AddWithValue("@emb", embeddingJson);
        cmd.Parameters.AddWithValue("@type", (int)entry.Type);
        cmd.Parameters.AddWithValue("@status", (int)entry.Status);
        cmd.Parameters.AddWithValue("@created", entry.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@accessed", entry.LastAccessedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@count", entry.AccessCount);
        cmd.Parameters.AddWithValue("@decay", entry.DecayFactor);
        cmd.Parameters.AddWithValue("@conf", entry.Confidence);
        cmd.Parameters.AddWithValue("@tags", JsonSerializer.Serialize(entry.Tags));
        cmd.Parameters.AddWithValue("@related", entry.RelatedTaskId?.ToString() ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryEntry>> RecallAsync(string query, int topK, CancellationToken ct)
    {
        EnsureInitialized();
        var queryEmbedding = await embedding.EmbedAsync(query, ct);
        var all = await LoadAllActiveAsync(ct);

        // 内存中计算余弦相似度并排序（置信度低于阈值排除）
        var ranked = all
            .Where(e => e.Confidence >= ConfidenceThreshold)
            .Select(e => (entry: e, score: embedding.CosineSimilarity(queryEmbedding, e.Embedding)))
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => x.entry)
            .ToList();

        // 更新 AccessCount 和 LastAccessedAt
        foreach (var entry in ranked)
        {
            await UpdateAccessAsync(entry.Id, ct);
        }

        return ranked;
    }

    public async Task ForgetAsync(Guid entryId, CancellationToken ct)
    {
        EnsureInitialized();
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "DELETE FROM memories WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", entryId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompactAsync(CancellationToken ct)
    {
        EnsureInitialized();
        // 删除权重极低（衰减后几乎为零）的记忆
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            DELETE FROM memories
            WHERE (access_count * EXP(-decay_factor * CAST(
                (julianday('now') - julianday(created_at)) AS REAL))) < 0.01
            AND status != 0
            """;
        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("记忆压缩完成，清理 {Count} 条低权重记忆", deleted);
        }
    }

    public async Task<IReadOnlyList<MemoryEntry>> RecallByTagAsync(string tag, CancellationToken ct)
    {
        EnsureInitialized();
        var all = await LoadAllActiveAsync(ct);
        return [.. all.Where(e => e.Tags.Contains(tag))];
    }

    /// <summary>
    /// DI 容器默认同步释放单例；必须实现 IDisposable（不能只 IAsyncDisposable）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_conn != null)
        {
            _conn.Dispose();
            _conn = null;
        }

        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_conn != null)
        {
            await _conn.DisposeAsync().ConfigureAwait(false);
            _conn = null;
        }

        GC.SuppressFinalize(this);
    }

    private static MemoryEntry MapRow(SqliteDataReader r)
    {
        var embJson = r.GetString(r.GetOrdinal("embedding"));
        var floats = JsonSerializer.Deserialize<float[]>(embJson) ?? [];
        return new MemoryEntry
        {
            Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
            Content = r.GetString(r.GetOrdinal("content")),
            Embedding = new ReadOnlyMemory<float>(floats),
            Type = (MemoryType)r.GetInt32(r.GetOrdinal("type")),
            Status = (MemoryStatus)r.GetInt32(r.GetOrdinal("status")),
            CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("created_at"))),
            LastAccessedAt = DateTime.Parse(r.GetString(r.GetOrdinal("last_accessed_at"))),
            AccessCount = r.GetInt32(r.GetOrdinal("access_count")),
            DecayFactor = r.GetDouble(r.GetOrdinal("decay_factor")),
            Confidence = r.GetDouble(r.GetOrdinal("confidence")),
            Tags = JsonSerializer.Deserialize<List<string>>(r.GetString(r.GetOrdinal("tags"))) ?? [],
            RelatedTaskId = r.IsDBNull(r.GetOrdinal("related_task_id"))
                ? null
                : Guid.Parse(r.GetString(r.GetOrdinal("related_task_id")))
        };
    }

    private async Task CreateSchemaAsync(CancellationToken ct)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS memories (
                id TEXT PRIMARY KEY,
                content TEXT NOT NULL,
                embedding TEXT NOT NULL,
                type INTEGER NOT NULL,
                status INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                last_accessed_at TEXT NOT NULL,
                access_count INTEGER NOT NULL DEFAULT 0,
                decay_factor REAL NOT NULL DEFAULT 0.01,
                confidence REAL NOT NULL DEFAULT 1.0,
                tags TEXT NOT NULL DEFAULT '[]',
                related_task_id TEXT
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<List<MemoryEntry>> LoadAllActiveAsync(CancellationToken ct)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT * FROM memories WHERE status = 0";
        var result = new List<MemoryEntry>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(MapRow(reader));
        }
        return result;
    }

    private async Task UpdateAccessAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            UPDATE memories
            SET access_count = access_count + 1, last_accessed_at = @now
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private void EnsureInitialized()
    {
        if (_conn == null)
        {
            throw new InvalidOperationException("SqliteMemoryStore 未初始化，请先调用 InitializeAsync");
        }
    }
}
