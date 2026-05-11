using AgentOrchestrator.Agents.Base;
using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;

namespace AgentOrchestrator.Agents.BuiltIn;

/// <summary>
/// Planner Agent：需求拆分、模块设计、文件规划、风险评估。
/// 输出结构化 JSON（modules/file_plan/steps/risks/definition_of_done）。
/// </summary>
public class PlannerAgent(ILLMClient llmClient) : AgentBase(llmClient)
{
    private const string SystemPrompt = """
        你是一个软件架构规划师。根据需求文档，输出严格的 JSON 格式规划（不得有任何 markdown 包裹）：
        {
          "modules": [{"name": "", "responsibility": "", "dependencies": []}],
          "file_plan": [{"path": "", "action": "create|modify|delete", "rationale": ""}],
          "steps": [{"order": 0, "description": "", "agent": "dev|test|critique"}],
          "risks": [{"description": "", "mitigation": "", "severity": "low|medium|high|critical"}],
          "definition_of_done": [""],
          "notes": ""
        }

        关键约束：
        1. steps 数组最多 5 项，聚合相关文件到同一个 dev 步骤，禁止每个文件单独一步。
        2. 典型结构：1 个 dev（核心实现）+ 1 个 dev（测试代码）+ 1 个 test + 1 个 critique。
        3. 不允许生成超过 5 个 steps，多余步骤合并到最近的同类型步骤。
        """;

    public override string Name => "Planner";
    public override string Version => "1.0";
    public override IReadOnlySet<string> Capabilities => new HashSet<string> { "plan" };

    public override async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var input = ctx.Workspace.Exists(ctx.Task.InputRef)
            ? await ctx.Workspace.ReadAsync(ctx.Task.InputRef, ct)
            : ctx.Task.InputRef;

        // 从记忆中检索相关历史决策
        var memories = await ctx.Memory.RecallAsync($"plan {input[..Math.Min(200, input.Length)]}", topK: 3, ct);
        var memoryContext = memories.Count > 0
            ? "\n\n历史决策参考：\n" + string.Join("\n", memories.Select(m => $"- {m.Content}"))
            : string.Empty;

        var spec = new InvocationSpec(
            ModelId: ctx.Task.Tags.GetValueOrDefault("modelId", "claude-sonnet-4-5"),
            SystemPrompt: SystemPrompt,
            UserPrompt: $"需求文档：\n{input}{memoryContext}",
            MaxTokens: 4096);

        var plan = await CallLLMWithSchemaAsync<PlannerOutput>(spec, ctx, ct);
        if (plan == null)
        {
            return AgentResult.Fail("Planner 未能生成合法计划");
        }

        // 持久化规划结果
        var planPath = $"plans/{ctx.Task.Id:N}.json";
        await ctx.Workspace.WriteAsync(planPath,
            System.Text.Json.JsonSerializer.Serialize(plan, JsonOptions), ct);

        // 将规划决策写入语义记忆
        await ctx.Memory.StoreAsync(new MemoryEntry
        {
            Content = $"规划决策: {plan.Notes ?? "无备注"}，步骤数={plan.Steps?.Length ?? 0}",
            Type = MemoryType.Decision,
            Tags = ["plan", ctx.Task.Id.ToString()],
            RelatedTaskId = ctx.Task.Id,
            Confidence = 0.9
        }, ct);

        // 根据规划步骤生成后续任务
        var nextTasks = GenerateNextTasks(plan, ctx.Task);

        return AgentResult.Succeed(
            $"规划完成: {plan.Modules?.Length ?? 0} 个模块, {plan.Steps?.Length ?? 0} 步",
            nextTasks)
            with
        { Artifacts = [new Artifact(planPath, "plan", string.Empty, 0, DateTime.UtcNow)] };
    }

    private static List<AgentTask> GenerateNextTasks(PlannerOutput plan, AgentTask parent)
    {
        if (plan.Steps == null || plan.Steps.Length == 0)
        {
            return [];
        }

        return [.. plan.Steps.Select(step => new AgentTask
        {
            Type = step.Agent ?? "dev",
            InputRef = $"plans/{parent.Id:N}.json",
            ParentTaskId = parent.Id,
            Tags = new Dictionary<string, string>
            {
                ["stepOrder"] = step.Order.ToString(),
                ["stepDesc"] = step.Description ?? string.Empty
            }
        })];
    }

    private record PlannerOutput(
        PlanModule[]? Modules,
        FilePlanItem[]? FilePlan,
        PlanStep[]? Steps,
        PlanRisk[]? Risks,
        string[]? DefinitionOfDone,
        string? Notes);

    private record PlanModule(string Name, string Responsibility, string[] Dependencies);
    private record FilePlanItem(string Path, string Action, string Rationale);
    private record PlanStep(int Order, string? Description, string? Agent);
    private record PlanRisk(string Description, string Mitigation, string Severity);
}
