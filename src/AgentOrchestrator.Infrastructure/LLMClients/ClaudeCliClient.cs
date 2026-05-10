using System.Runtime.CompilerServices;
using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentOrchestrator.Infrastructure.LLMClients;

/// <summary>
/// Claude Code CLI 客户端（claude 2.x）。
/// 非交互调用格式：
///   claude -p --model &lt;model&gt; --no-session-persistence --output-format text
/// prompt 统一通过 stdin 传入，避免多行 prompt 进入 Windows cmd 参数解析和日志。
/// </summary>
public class ClaudeCliClient(
    string cliPath,
    IToolSandbox sandbox,
    ILogger<ClaudeCliClient> logger,
    TimeSpan? timeout = null) : ILLMClient
{
    public string ProviderName => "claude-cli";
    private TimeSpan Timeout { get; } = timeout ?? TimeSpan.FromSeconds(120);

    public IReadOnlySet<string> SupportedModels => new HashSet<string>
    {
        "claude-opus-4-5", "claude-sonnet-4-5", "claude-haiku-4-5",
        "claude-3-7-sonnet-latest", "claude-3-5-haiku-latest",
        // 短别名（claude CLI 支持）
        "opus", "sonnet", "haiku",
    };

    public async Task<LLMResponse> ExecuteAsync(InvocationSpec spec, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var args = BuildArguments(spec);
        var stdinContent = BuildPrompt(spec);

        var inv = new ToolInvocation(
            Command: cliPath,
            Arguments: args,
            WorkingDirectory: Path.GetTempPath(),
            // LLM CLI 调用无文件操作，AllowedPaths 留空（sandbox 不做路径校验）
            Environment: [],
            Timeout: Timeout,
            StdInput: stdinContent,
            AllowedPaths: new HashSet<string>());

        var result = await sandbox.ExecuteAsync(inv, ct);
        sw.Stop();

        if (!result.IsSuccess)
        {
            logger.LogError("Claude CLI 失败 ExitCode={Code} Err={Err}",
                result.ExitCode, result.StdErrSnippet());
            throw new LLMClientException(
                $"Claude CLI 失败 (exit={result.ExitCode}): {result.StdErrSnippet()}");
        }

        var content = result.StdOut.Trim();
        var usage = EstimateTokenUsage(spec, content, spec.ModelId);
        return new LLMResponse(content, usage, sw.Elapsed, spec.ModelId, "stop");
    }

    public async IAsyncEnumerable<LLMToken> StreamAsync(
        InvocationSpec spec,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var response = await ExecuteAsync(spec, ct);
        foreach (var chunk in ChunkString(response.Content, 60))
        {
            yield return new LLMToken(chunk);
        }

        yield return new LLMToken(string.Empty, IsLast: true);
    }

    public Task<ModelCapability> GetCapabilityAsync(string modelId, CancellationToken ct) =>
        Task.FromResult(new ModelCapability(
            modelId,
            MaxContextTokens: modelId.Contains("opus") ? 200_000 : 100_000,
            SupportsStructuredOutput: true,
            SupportsStreaming: false,
            CostPer1KPromptTokens: modelId.Contains("opus") ? 0.015 : 0.003,
            CostPer1KCompletionTokens: modelId.Contains("opus") ? 0.075 : 0.015));

    /// <summary>
    /// 构建 claude CLI 参数数组。prompt 不进入参数，统一走 stdin。
    /// </summary>
    private static string[] BuildArguments(InvocationSpec spec)
    {
        var args = new List<string>
        {
            "-p",                            // 非交互打印模式
            "--model", spec.ModelId,
            "--no-session-persistence",      // 不保存会话到磁盘
            "--output-format", "text",       // 纯文本输出，便于后续 JSON 提取
        };

        return [.. args];
    }

    private static string BuildPrompt(InvocationSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.SystemPrompt))
        {
            return spec.UserPrompt;
        }

        return $"""
            <system>
            {spec.SystemPrompt}
            </system>

            <user>
            {spec.UserPrompt}
            </user>
            """;
    }

    private static TokenUsage EstimateTokenUsage(InvocationSpec spec, string output, string modelId)
    {
        var prompt = (spec.SystemPrompt.Length + spec.UserPrompt.Length) / 4;
        var completion = output.Length / 4;
        var cost = ((prompt * 0.003) + (completion * 0.015)) / 1000.0;
        return new TokenUsage(prompt, completion, modelId, cost);
    }

    private static IEnumerable<string> ChunkString(string s, int size)
    {
        for (var i = 0; i < s.Length; i += size)
        {
            yield return s.Substring(i, Math.Min(size, s.Length - i));
        }
    }
}
