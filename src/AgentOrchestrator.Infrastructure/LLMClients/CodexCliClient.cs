using System.Runtime.CompilerServices;
using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentOrchestrator.Infrastructure.LLMClients;

/// <summary>
/// Codex CLI 客户端（codex-cli 0.x）。
/// 非交互调用格式：
///   codex --ask-for-approval never exec -m &lt;model&gt; --ephemeral -s read-only -
/// prompt 统一通过 stdin 传入，避免多行 prompt 进入 Windows cmd 参数解析和日志。
/// </summary>
public class CodexCliClient(
    string cliPath,
    IToolSandbox sandbox,
    ILogger<CodexCliClient> logger,
    TimeSpan? timeout = null) : ILLMClient
{
    public string ProviderName => "codex-cli";
    private TimeSpan Timeout { get; } = timeout ?? TimeSpan.FromSeconds(120);

    public IReadOnlySet<string> SupportedModels => new HashSet<string>
    {
        "gpt-4o", "gpt-4o-mini", "o3", "o4-mini", "codex-mini-latest",
    };

    public async Task<LLMResponse> ExecuteAsync(InvocationSpec spec, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var args = BuildArguments(spec);
        var stdinContent = BuildPrompt(spec);

        // 输出最终消息到临时文件，再读取（避免 JSON 日志混入 stdout）
        var outFile = Path.Combine(Path.GetTempPath(), $"codex-out-{Guid.NewGuid():N}.txt");
        args = [.. args, "--output-last-message", outFile, "-"];

        var inv = new ToolInvocation(
            Command: cliPath,
            Arguments: args,
            WorkingDirectory: Path.GetTempPath(),
            Environment: [],
            Timeout: Timeout,
            StdInput: stdinContent,
            AllowedPaths: new HashSet<string>());

        try
        {
            var result = await sandbox.ExecuteAsync(inv, ct);
            sw.Stop();

            if (!result.IsSuccess)
            {
                logger.LogError("Codex CLI 失败 ExitCode={Code} Err={Err}",
                    result.ExitCode, result.StdErrSnippet());
                throw new LLMClientException(
                    $"Codex CLI 失败 (exit={result.ExitCode}): {result.StdErrSnippet()}");
            }

            // 优先读取 --output-last-message 文件，回退到 stdout
            var content = File.Exists(outFile)
                ? (await File.ReadAllTextAsync(outFile, ct)).Trim()
                : result.StdOut.Trim();

            var usage = EstimateTokenUsage(spec, content);
            return new LLMResponse(content, usage, sw.Elapsed, spec.ModelId, "stop");
        }
        finally
        {
            if (File.Exists(outFile))
            {
                File.Delete(outFile);
            }
        }
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
            MaxContextTokens: 128_000,
            SupportsStructuredOutput: true,
            SupportsStreaming: true,
            CostPer1KPromptTokens: modelId.Contains("mini") ? 0.00015 : 0.005,
            CostPer1KCompletionTokens: modelId.Contains("mini") ? 0.0006 : 0.015));

    /// <summary>
    /// 构建 codex exec 参数。prompt 不进入参数，统一用 '-' 从 stdin 读取。
    /// </summary>
    private static string[] BuildArguments(InvocationSpec spec)
    {
        var args = new List<string>
        {
            "--ask-for-approval", "never", // 全局参数，必须放在 exec 前
            "exec",
            "-m", spec.ModelId,
            "--ephemeral",          // 不持久化 session
            "-s", "read-only",       // LLM 客户端只需要生成文本，不应写文件
            "--skip-git-repo-check",
            "--color", "never",
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

    private static TokenUsage EstimateTokenUsage(InvocationSpec spec, string output)
    {
        var prompt = (spec.SystemPrompt.Length + spec.UserPrompt.Length) / 4;
        var completion = output.Length / 4;
        var cost = ((prompt * 0.005) + (completion * 0.015)) / 1000.0;
        return new TokenUsage(prompt, completion, spec.ModelId, cost);
    }

    private static IEnumerable<string> ChunkString(string s, int size)
    {
        for (var i = 0; i < s.Length; i += size)
        {
            yield return s.Substring(i, Math.Min(size, s.Length - i));
        }
    }
}
