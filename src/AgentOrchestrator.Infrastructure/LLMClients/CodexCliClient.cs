using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace AgentOrchestrator.Infrastructure.LLMClients;

/// <summary>
/// Codex CLI 客户端：通过 codex.exe 调用 OpenAI Codex/GPT 系列模型。
/// </summary>
public class CodexCliClient(
    string cliPath,
    IToolSandbox sandbox,
    ILogger<CodexCliClient> logger) : ILLMClient
{
    private const int StdinThresholdBytes = 4096;

    public string ProviderName => "codex-cli";

    public IReadOnlySet<string> SupportedModels => new HashSet<string>
    {
        "gpt-4o", "gpt-4o-mini", "o3", "o4-mini", "codex-mini-latest"
    };

    public async Task<LLMResponse> ExecuteAsync(InvocationSpec spec, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var prompt = BuildFullPrompt(spec);
        var (args, stdinContent) = BuildArguments(spec, prompt);

        var inv = new ToolInvocation(
            Command: cliPath,
            Arguments: args,
            WorkingDirectory: Path.GetTempPath(),
            Environment: new Dictionary<string, string>(),
            Timeout: TimeSpan.FromSeconds(120),
            StdInput: stdinContent,
            AllowedPaths: new HashSet<string> { Path.GetTempPath() });

        var result = await sandbox.ExecuteAsync(inv, ct);
        sw.Stop();

        if (!result.IsSuccess)
        {
            logger.LogError("Codex CLI 失败 ExitCode={Code}", result.ExitCode);
            throw new LLMClientException($"Codex CLI 失败 (exit={result.ExitCode}): {result.StdErrSnippet()}");
        }

        var content = result.StdOut.Trim();
        var usage = EstimateTokenUsage(spec, content);
        return new LLMResponse(content, usage, sw.Elapsed, spec.ModelId, "stop");
    }

    public async IAsyncEnumerable<LLMToken> StreamAsync(
        InvocationSpec spec,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var response = await ExecuteAsync(spec, ct);
        foreach (var chunk in ChunkString(response.Content, 50))
            yield return new LLMToken(chunk);
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

    private static string BuildFullPrompt(InvocationSpec spec) =>
        string.IsNullOrEmpty(spec.SystemPrompt)
            ? spec.UserPrompt
            : $"System: {spec.SystemPrompt}\n\nUser: {spec.UserPrompt}";

    private static (string[] args, string? stdin) BuildArguments(InvocationSpec spec, string prompt)
    {
        var args = new List<string> { "exec", "--model", spec.ModelId, "--approval-mode", "full-auto" };

        if (System.Text.Encoding.UTF8.GetByteCount(prompt) > StdinThresholdBytes)
            return ([.. args], prompt);

        args.AddRange(["-q", prompt]);
        return ([.. args], null);
    }

    private static TokenUsage EstimateTokenUsage(InvocationSpec spec, string output)
    {
        var prompt = (spec.SystemPrompt + spec.UserPrompt).Length / 4;
        var completion = output.Length / 4;
        var cost = (prompt * 0.005 + completion * 0.015) / 1000.0;
        return new TokenUsage(prompt, completion, spec.ModelId, cost);
    }

    private static IEnumerable<string> ChunkString(string s, int size)
    {
        for (int i = 0; i < s.Length; i += size)
            yield return s.Substring(i, Math.Min(size, s.Length - i));
    }
}