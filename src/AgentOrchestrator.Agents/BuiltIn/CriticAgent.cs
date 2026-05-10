using AgentOrchestrator.Agents.Base;
using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;

namespace AgentOrchestrator.Agents.BuiltIn;

/// <summary>
/// Critic Agent：代码审查，输出问题列表和改进建议。
/// </summary>
public class CriticAgent(ILLMClient llmClient) : AgentBase(llmClient)
{
    private const string SystemPrompt = """
        你是一个代码审查专家。审查代码变更，输出严格 JSON：
        {
          "issues": [{"file": "", "line": 0, "description": "", "severity": "low|medium|high|critical", "category": "bug|security|performance|style"}],
          "severity": "low|medium|high|critical",
          "suggestions": [""],
          "approved": false,
          "notes": ""
        }
        """;

    public override string Name => "Critic";
    public override string Version => "1.0";
    public override IReadOnlySet<string> Capabilities => new HashSet<string> { "critique", "review" };

    public override async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var codeContent = ctx.Workspace.Exists(ctx.Task.InputRef)
            ? await ctx.Workspace.ReadAsync(ctx.Task.InputRef, ct)
            : ctx.Task.InputRef;

        var spec = new InvocationSpec(
            ModelId: ctx.Task.Tags.GetValueOrDefault("modelId", "claude-opus-4-5"),
            SystemPrompt: SystemPrompt,
            UserPrompt: $"待审查代码/变更：\n{codeContent}",
            MaxTokens: 3000);

        var review = await CallLLMWithSchemaAsync<CriticOutput>(spec, ctx, ct);
        if (review == null)
        {
            return AgentResult.Fail("Critic 未能生成审查结果");
        }

        // 写入审查报告
        var reportPath = $"reports/critic-{ctx.Task.Id:N}.json";
        await ctx.Workspace.WriteAsync(reportPath,
            System.Text.Json.JsonSerializer.Serialize(review, JsonOptions), ct);

        if (!review.Approved && review.Issues?.Any(i => i.Severity is "high" or "critical") == true)
        {
            // 严重问题需回流 Developer
            var devTask = new AgentTask
            {
                Type = "dev",
                InputRef = ctx.Task.InputRef,
                ParentTaskId = ctx.Task.ParentTaskId,
                Tags = new Dictionary<string, string>(ctx.Task.Tags)
                {
                    ["criticIssues"] = string.Join("; ", review.Issues.Where(i => i.Severity is "high" or "critical").Select(i => i.Description))
                }
            };
            return AgentResult.Succeed($"发现 {review.Issues?.Length ?? 0} 个问题，需修复", [devTask]);
        }

        return AgentResult.Succeed($"审查通过，{review.Issues?.Length ?? 0} 个低优先级建议")
            with
        { Artifacts = [new Artifact(reportPath, "report", string.Empty, 0, DateTime.UtcNow)] };
    }

    private record CriticOutput(CriticIssue[]? Issues, string? Severity, string[]? Suggestions, bool Approved, string? Notes);
    private record CriticIssue(string File, int Line, string Description, string Severity, string Category);
}
