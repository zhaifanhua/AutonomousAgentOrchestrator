using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentOrchestrator.Infrastructure.Routing;

/// <summary>
/// 智能任务路由器。
/// 策略：任务类型规则优先 → 语义嵌入相似度匹配 → 历史成功率自适应调整模型选择。
/// </summary>
public class IntelligentTaskRouter(
    IEmbeddingService embeddingService,
    ILogger<IntelligentTaskRouter> logger) : ITaskRouter
{
    // 任务类型 → Agent 类型的静态规则
    private static readonly Dictionary<string, string> TypeToAgent = new(StringComparer.OrdinalIgnoreCase)
    {
        ["plan"] = "Planner",
        ["dev"] = "Developer",
        ["test"] = "Tester",
        ["critique"] = "Critic",
        ["reflect"] = "Reflector",
        ["gate"] = "Gate",
    };

    // 复杂度阈值：高复杂度任务用强模型
    private const double HighComplexityThreshold = 0.7;

    // 历史成功率追踪（AgentType → 成功率）
    private readonly Dictionary<string, double> _successRates = new();

    public async Task<RouteDecision> RouteAsync(AgentTask task, ProjectContext ctx, CancellationToken ct)
    {
        // 1. 规则路由：直接按任务类型匹配
        if (TypeToAgent.TryGetValue(task.Type, out var agentType))
        {
            var model = SelectModel(task, ctx, agentType);
            var confidence = 0.95f;
            logger.LogDebug("规则路由: Task={Type} → Agent={Agent} Model={Model}", task.Type, agentType, model);
            return new RouteDecision(agentType, model, confidence, BuildHints(task, ctx));
        }

        // 2. 语义路由：计算任务描述与 Agent 能力的相似度
        var semanticAgent = await SemanticRouteAsync(task, ct);
        var semanticModel = SelectModel(task, ctx, semanticAgent);
        logger.LogDebug("语义路由: Task={Type} → Agent={Agent} Model={Model}", task.Type, semanticAgent, semanticModel);

        return new RouteDecision(semanticAgent, semanticModel, 0.6f, BuildHints(task, ctx));
    }

    /// <summary>
    /// 根据任务复杂度和历史成功率选择模型
    /// </summary>
    private string SelectModel(AgentTask task, ProjectContext ctx, string agentType)
    {
        var complexity = EstimateComplexity(task, ctx);
        var successRate = _successRates.GetValueOrDefault(agentType, 1.0);

        // 低成功率或高复杂度 → 使用强模型
        if (successRate < 0.5 || complexity > HighComplexityThreshold)
            return agentType switch
            {
                "Planner" or "Critic" or "Reflector" => "claude-opus-4-5",
                _ => "claude-sonnet-4-5"
            };

        // 正常路径：轻量任务用快速模型
        return agentType switch
        {
            "Planner" or "Critic" or "Reflector" => "claude-sonnet-4-5",
            "Developer" or "Tester" => "gpt-4o-mini",
            _ => "mock-model"
        };
    }

    private static double EstimateComplexity(AgentTask task, ProjectContext ctx)
    {
        // 基于文件复杂度评分和任务标签估算
        if (task.Tags.TryGetValue("complexity", out var val) && double.TryParse(val, out var c))
            return c;

        var inputComplexity = ctx.ComplexityScores.GetValueOrDefault(task.InputRef, 0.5);
        return inputComplexity;
    }

    private async Task<string> SemanticRouteAsync(AgentTask task, CancellationToken ct)
    {
        // 计算任务与各 Agent 描述的嵌入相似度
        var taskEmbedding = await embeddingService.EmbedAsync(task.Type + " " + task.InputRef, ct);
        var best = ("Developer", 0f);

        var agentDescriptions = new Dictionary<string, string>
        {
            ["Planner"] = "plan design module architecture requirements",
            ["Developer"] = "implement code write function class method",
            ["Tester"] = "test verify validate assert check quality",
            ["Critic"] = "review critique analyze code quality issues",
            ["Reflector"] = "reflect learn lessons strategy meta cognition",
        };

        foreach (var (agent, desc) in agentDescriptions)
        {
            var descEmbedding = await embeddingService.EmbedAsync(desc, ct);
            var sim = embeddingService.CosineSimilarity(taskEmbedding, descEmbedding);
            if (sim > best.Item2) best = (agent, sim);
        }

        return best.Item1;
    }

    /// <summary>
    /// 更新 Agent 历史成功率（用于自适应调整）
    /// </summary>
    public void RecordOutcome(string agentType, bool success)
    {
        var current = _successRates.GetValueOrDefault(agentType, 1.0);
        // 指数移动平均（α=0.2）
        _successRates[agentType] = current * 0.8 + (success ? 1.0 : 0.0) * 0.2;
    }

    private static Dictionary<string, object> BuildHints(AgentTask task, ProjectContext ctx) => new()
    {
        ["knownDefectsCount"] = ctx.KnownDefects.Count,
        ["attempt"] = task.Attempt,
        ["hasParent"] = task.ParentTaskId.HasValue,
    };
}