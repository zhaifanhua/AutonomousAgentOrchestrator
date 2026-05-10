using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using AgentOrchestrator.Core.Observability;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.Agents.Base;

/// <summary>
/// Agent 基类，提供：
/// - LLM 调用（带 JSON Schema 校验，校验失败最多重试 3 次）
/// - 失败签名标准化（用于无进展检测）
/// - 链路追踪（ActivitySource）
/// </summary>
public abstract class AgentBase(ILLMClient llmClient) : IAgent
{
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public abstract string Name { get; }
    public abstract string Version { get; }
    public abstract IReadOnlySet<string> Capabilities { get; }
    protected ILLMClient LlmClient { get; } = llmClient;

    public abstract Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct);

    public virtual Task<bool> CanHandleAsync(AgentTask task, CancellationToken ct) =>
        Task.FromResult(Capabilities.Contains(task.Type));

    /// <summary>
    /// 从混合文本中提取第一个完整 JSON 对象
    /// </summary>
    protected static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text.Trim();
    }

    /// <summary>
    /// 生成标准化失败签名（用于无进展检测）
    /// </summary>
    protected static string ComputeFailureSignature(string stderr, string stdout)
    {
        var raw = $"{stderr?.Trim()[..Math.Min(200, stderr?.Length ?? 0)]}{stdout?.Trim()[..Math.Min(200, stdout?.Length ?? 0)]}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>
    /// 调用 LLM 并解析 JSON 结果，校验失败自动重试（带错误反馈）
    /// </summary>
    protected async Task<T?> CallLLMWithSchemaAsync<T>(
        InvocationSpec spec,
        AgentContext ctx,
        CancellationToken ct,
        int maxRetries = 3) where T : class
    {
        using var activity = OrchestratorMetrics.ActivitySource.StartActivity($"LLM.{Name}");
        activity?.SetTag("taskId", ctx.Task.Id.ToString());
        activity?.SetTag("model", spec.ModelId);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? lastError = null;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            // 重试时将上次的错误反馈追加到 prompt
            var currentSpec = lastError != null
                ? spec with { UserPrompt = spec.UserPrompt + $"\n\n上次输出校验失败，错误：{lastError}\n请修正后重新输出合法 JSON。" }
                : spec;

            try
            {
                var response = await LlmClient.ExecuteAsync(currentSpec, ct);
                sw.Stop();

                OrchestratorMetrics.LLMCallDurationSeconds.Record(sw.Elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>("model", spec.ModelId),
                    new KeyValuePair<string, object?>("agent", Name));
                OrchestratorMetrics.LLMTokensConsumed.Add(response.Usage.Total,
                    new KeyValuePair<string, object?>("model", spec.ModelId));
                OrchestratorMetrics.LLMCostDollars.Add(response.Usage.CostEstimate,
                    new KeyValuePair<string, object?>("model", spec.ModelId));

                // 尝试从响应中提取 JSON
                var json = ExtractJson(response.Content);
                var result = JsonSerializer.Deserialize<T>(json, JsonOptions);
                if (result != null)
                {
                    return result;
                }

                lastError = "反序列化结果为 null";
            }
            catch (JsonException ex)
            {
                lastError = ex.Message;
                ctx.Logger.LogWarning("JSON 校验失败 (attempt {A}/{Max}): {Err}", attempt + 1, maxRetries, ex.Message);
            }
        }

        ctx.Logger.LogError("LLM 调用 {MaxRetries} 次均失败", maxRetries);
        return null;
    }
}
