using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace AgentOrchestrator.Infrastructure.LLMClients;

/// <summary>
/// Claude CLI 客户端：通过 claude.exe 调用 Claude 模型。
/// prompt 超过阈值时自动走 stdin 传递，避免命令行参数过长。
/// </summary>
public class ClaudeCliClient(
    string cliPath,
    IToolSandbox sandbox,
    ILogger<ClaudeCliClient> logger) : ILLMClient
{
    private const int StdinThresholdBytes = 4096;

    public string ProviderName => "claude-cli";

    public IReadOnlySet<string> SupportedModels => new HashSet<string>
    {
        "claude-opus-4-5", "claude-sonnet-4-5", "claude-haiku-4-5",
        "claude-3-7-sonnet-latest", "claude-3-5-haiku-latest"
    };

    public async Task<LLMResponse> ExecuteAsync(InvocationSpec spec, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var prompt = BuildPrompt(spec);
        var args = BuildArguments(spec, prompt, out var stdinContent);

        var inv = new ToolInvocation(
            Command: cliPath,
            Arguments: args,
            WorkingDirectory: Path.GetTempPath(),
            Environment: [],
            Timeout: TimeSpan.FromSeconds(120),
            StdInput: stdinContent,
            AllowedPaths: new HashSet<string> { Path.GetTempPath() });

        var result = await sandbox.ExecuteAsync(inv, ct);
        sw.Stop();

        if (!result.IsSuccess)
        {
            logger.LogError("Claude CLI 失败 ExitCode={Code} Err={Err}",
                result.ExitCode, result.StdErrSnippet());
            throw new LLMClientException($"Claude CLI 失败 (exit={result.ExitCode}): {result.StdErrSnippet()}");
        }

        var content = result.StdOut.Trim();
        var usage = EstimateTokenUsage(spec, content, spec.ModelId);
        return new LLMResponse(content, usage, sw.Elapsed, spec.ModelId, "stop");
    }

    public async IAsyncEnumerable<LLMToken> StreamAsync(
        InvocationSpec spec,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Claude CLI 不原生支持流式，逐字符模拟返回
        var response = await ExecuteAsync(spec, ct);
        foreach (var chunk in ChunkString(response.Content, 50))
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

    private static string BuildPrompt(InvocationSpec spec) =>
        string.IsNullOrEmpty(spec.SystemPrompt)
            ? spec.UserPrompt
            : $"{spec.SystemPrompt}\n\n{spec.UserPrompt}";

    private static string[] BuildArguments(InvocationSpec spec, string prompt, out string? stdinContent)
    {
        var args = new List<string> { "--model", spec.ModelId };
        if (spec.MaxTokens > 0)
        {
            args.AddRange(["--max-tokens", spec.MaxTokens.ToString()]);
        }

        // 长 prompt 走 stdin，短 prompt 走命令行
        if (System.Text.Encoding.UTF8.GetByteCount(prompt) > StdinThresholdBytes)
        {
            stdinContent = prompt;
            args.Add("--stdin");
        }
        else
        {
            stdinContent = null;
            args.AddRange(["-p", prompt]);
        }

        return [.. args];
    }

    private static TokenUsage EstimateTokenUsage(InvocationSpec spec, string output, string modelId)
    {
        // 粗略估算：4 字符 ≈ 1 token
        var prompt = (spec.SystemPrompt + spec.UserPrompt).Length / 4;
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
