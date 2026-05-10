using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AgentOrchestrator.Infrastructure.Persistence;

/// <summary>
/// 基于 JSON 文件的状态持久化。
/// 写入策略：先写临时文件 → 校验 JSON → File.Move（原子替换）。
/// </summary>
public class JsonStateStore(string workspacePath, ILogger<JsonStateStore> logger) : IStateStore
{
    private readonly string _stateFile = Path.Combine(workspacePath, "state.json");
    private readonly string _stateTempFile = Path.Combine(workspacePath, "state.json.tmp");
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<OrchestratorState?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_stateFile))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_stateFile, ct);
            var state = JsonSerializer.Deserialize<OrchestratorState>(json, JsonOptions);
            logger.LogInformation("状态已加载，版本={Version}, 队列长度={QueueLen}",
                state?.Version, state?.Queue.Count);
            return state;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "加载 state.json 失败，路径={Path}", _stateFile);
            throw;
        }
    }

    public async Task SaveAsync(OrchestratorState state, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            // 先写临时文件
            var json = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(_stateTempFile, json, ct);

            // 校验 JSON 合法性
            JsonSerializer.Deserialize<OrchestratorState>(json, JsonOptions);

            // 原子替换
            File.Move(_stateTempFile, _stateFile, overwrite: true);
            logger.LogDebug("状态已保存，版本={Version}", state.Version);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存 state.json 失败");
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<long> GetVersionAsync(CancellationToken ct)
    {
        var state = await LoadAsync(ct);
        return state?.Version ?? 0;
    }
}