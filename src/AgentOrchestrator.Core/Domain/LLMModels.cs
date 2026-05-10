using System.Text.Json.Nodes;

namespace AgentOrchestrator.Core.Domain;

/// <summary>
/// LLM 调用规格
/// </summary>
public record InvocationSpec(
    string ModelId,
    string SystemPrompt,
    string UserPrompt,
    int MaxTokens = 4096,
    float Temperature = 0.2f,
    JsonNode? ResponseSchema = null,
    Dictionary<string, object>? ProviderOptions = null);

/// <summary>
/// LLM 响应
/// </summary>
public record LLMResponse(
    string Content,
    TokenUsage Usage,
    TimeSpan Duration,
    string ModelId,
    string FinishReason);

/// <summary>
/// 流式 Token（流式调用使用）
/// </summary>
public record LLMToken(string Delta, bool IsLast = false);

/// <summary>
/// 模型能力描述
/// </summary>
public record ModelCapability(
    string ModelId,
    int MaxContextTokens,
    bool SupportsStructuredOutput,
    bool SupportsStreaming,
    double CostPer1KPromptTokens,
    double CostPer1KCompletionTokens);

/// <summary>
/// 路由决策
/// </summary>
public record RouteDecision(
    string AgentType,
    string ModelId,
    float Confidence,
    Dictionary<string, object> Hints);

/// <summary>
/// 模型降级策略
/// </summary>
public record ModelFallbackPolicy(
    string PrimaryModel,
    List<string> FallbackModels,
    int MaxRetries = 2);