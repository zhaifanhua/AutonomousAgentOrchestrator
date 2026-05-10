using AgentOrchestrator.Agents.Base;
using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;

namespace AgentOrchestrator.Agents.BuiltIn;

/// <summary>
/// Reflector Agent：元认知分析，评估执行历史并提出策略调整建议。
/// 通常在多次失败后由 Orchestrator 自动插入。
/// </summary>
public class ReflectorAgent(ILLMClient llmClient) : AgentBase(llmClient)
{
    public override string Name => "Reflector";
    public override string Version => "1.0";
    public override IReadOnlySet<string> Capabilities => new HashSet<string> { "reflect" };

    private const string SystemPrompt = """
        你是一个 AI 系统的元认知分析师。分析执行历史，输出策略调整建议，严格 JSON：
        {
          "strategy_adjustments": ["具体调整建议"],
          "lessons": ["学到的经验教训"],
          "should_continue": true,
          "recommended_model_change": null,
          "notes": ""
        }
        """;

    public override async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        // 从记忆中检索历史错误模式和决策
        var errorPatterns = await ctx.Memory.RecallAsync("error failure bug", topK: 5, ct);
        var decisions = await ctx.Memory.RecallAsync("decision plan strategy", topK: 3, ct);

        var historyContext = string.Join("\n",
            errorPatterns.Select(m => $"错误: {m.Content}").Concat(
            decisions.Select(m => $"决策: {m.Content}")));

        var spec = new InvocationSpec(
            ModelId: ctx.Task.Tags.GetValueOrDefault("modelId", "claude-opus-4-5"),
            SystemPrompt: SystemPrompt,
            UserPrompt: $"执行历史摘要：\n{historyContext}\n\n当前任务尝试次数：{ctx.Task.Attempt}",
            MaxTokens: 2000);

        var reflection = await CallLLMWithSchemaAsync<ReflectorOutput>(spec, ctx, ct);
        if (reflection == null)
            return AgentResult.Fail("Reflector 未能生成反思结果");

        // 将经验教训写入长期记忆
        foreach (var lesson in reflection.Lessons ?? [])
        {
            await ctx.Memory.StoreAsync(new MemoryEntry
            {
                Content = lesson,
                Type = MemoryType.Lesson,
                Tags = ["reflect", "lesson"],
                RelatedTaskId = ctx.Task.Id,
                Confidence = 0.75
            }, ct);
        }

        // 定期压缩记忆（去除低权重条目）
        await ctx.Memory.CompactAsync(ct);

        return AgentResult.Succeed(
            $"反思完成: {reflection.Lessons?.Length ?? 0} 条教训, 继续={reflection.ShouldContinue}");
    }

    private record ReflectorOutput(
        string[]? StrategyAdjustments,
        string[]? Lessons,
        bool ShouldContinue,
        string? RecommendedModelChange,
        string? Notes);
}