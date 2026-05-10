**角色**：资深 .NET 架构师与 AI 工程负责人。
**目标**：在 **C# .NET 10** 中实现一个**事件驱动、智能路由、语义记忆、全链路可观测**的「Autonomous Agent Orchestrator」，通过 **CLI（Claude / Codex / 通用 LLM）** 调用模型，支持多模型协同与自适应收敛。

---

## 一、事实约束（必须遵守）

1. **编排层不依赖模型能力**：调度、重试、收敛、持久化、审计、记忆管理全部由 **C# 编排层**完成，模型仅作为"推理执行单元"。
2. **CLI 是唯一模型入口**：抽象为 `ILLMClient.ExecuteAsync(InvocationSpec, CancellationToken)`，禁止在业务层散落 `Process.Start`。
3. **文件系统是单一事实来源（SoT）**：代码、计划、测评报告、`state.json`、证据日志、记忆快照必须在磁盘可回放；内存仅为热缓存（LRU + TTL）。
4. **输出必须是机器可读协议**：优先 JSON Schema / Protocol Buffers；禁止「散文式输出」作为唯一控制流。
5. **安全第一**：禁止未转义的 shell 拼接；长 prompt 走 stdin 或临时文件；所有文件操作限制在 `pathsAllowlist` 白名单内。

---

## 二、核心架构：事件驱动管道

```
┌──────────┐    ┌──────────────┐    ┌──────────────┐    ┌──────────────┐
│  Input   │───▶│  Intelligent │───▶│  Agent       │───▶│  Result      │
│  Queue   │    │  Router      │    │  Executor    │    │  Handler     │
└──────────┘    └──────────────┘    └──────────────┘    └──────────────┘
                      │                    │                    │
                      ▼                    ▼                    ▼
               ┌──────────────┐    ┌──────────────┐    ┌──────────────┐
               │  Semantic    │    │  Tool/CLI    │    │  State       │
               │  Memory      │    │  Sandbox     │    │  Manager     │
               └──────────────┘    └──────────────┘    └──────────────┘
```

### 2.1 事件总线（第一优先级，非第二阶段）

```csharp
public interface IEventBus
{
    Task PublishAsync<T>(T @event, CancellationToken ct) where T : DomainEvent;
    IDisposable Subscribe<T>(Func<T, Task> handler) where T : DomainEvent;
}

// 核心事件
public record TaskDequeued(Guid TaskId, string Type, DateTime At) : DomainEvent;
public record AgentExecutionStarted(Guid TaskId, string AgentType, DateTime At) : DomainEvent;
public record AgentExecutionCompleted(Guid TaskId, AgentResult Result, TimeSpan Duration) : DomainEvent;
public record StateTransitioned(Guid TaskId, AgentTaskStatus From, AgentTaskStatus To, string Reason) : DomainEvent;
public record ConvergenceCheckTriggered(int Iteration, bool NoProgress, string Signature) : DomainEvent;
public record BudgetExceeded(string LimitType, double Current, double Max) : DomainEvent;
```

- **中间件管道**：`Middleware → Agent → Middleware`（日志、指标、缓存、限流、审计全部以中间件形式挂载）。
- **背压与流控**：基于 `System.Threading.Channels` 的有界通道 + 信号量控制并行度。

### 2.2 智能任务路由

```csharp
public interface ITaskRouter
{
    Task<RouteDecision> RouteAsync(AgentTask task, ProjectContext ctx, CancellationToken ct);
}

public record RouteDecision(
    string AgentType,              // 目标 Agent
    string ModelId,                // 推荐模型（根据复杂度/成本）
    float Confidence,              // 路由置信度
    Dictionary<string, object> Hints  // 传递给 Agent 的提示
);
```

- **路由策略**：基于任务语义嵌入（`IEmbeddingService`）的相似度匹配 + 规则引擎回退。
- **多模型策略**：轻量任务走 Haiku/GPT-4o-mini，复杂推理走 Opus/GPT-4o；支持 `ModelFallbackPolicy`。
- **热度感知**：高频失败路径自动降级路由或插入 Critic Agent。

---

## 三、核心抽象（代码契约 v2）

### 3.1 任务与结果

```csharp
public record AgentTask
{
    public Guid Id { get; init; }
    public string Type { get; init; }          // plan | dev | test | critique | reflect | gate
    public string InputRef { get; init; }       // 输入文件路径（相对于 workspace）
    public AgentTaskStatus Status { get; init; }
    public int Attempt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public List<string> ArtifactRefs { get; init; }
    public Dictionary<string, string> Tags { get; init; }  // 可扩展标签
    public Guid? ParentTaskId { get; init; }                // DAG 依赖
    public List<Guid> DependsOn { get; init; }              // 前置任务
}

public record AgentResult
{
    public bool Success { get; init; }
    public string Summary { get; init; }
    public List<Artifact> Artifacts { get; init; }
    public List<AgentTask> NextTasks { get; init; }
    public Diagnostics Diagnostics { get; init; }
    public TokenUsage TokenUsage { get; init; }
    public TimeSpan WallTime { get; init; }
}

public record Diagnostics
{
    public int ExitCode { get; init; }
    public string StdErrSnippet { get; init; }
    public string StdOutSnippet { get; init; }
    public List<string> Warnings { get; init; }
}

public record TokenUsage(int Prompt, int Completion, string ModelId, double CostEstimate);
```

### 3.2 项目上下文

```csharp
public record ProjectContext
{
    public string RequirementSummary { get; init; }
    public List<string> PathsAllowlist { get; init; }
    public List<KnownDefect> KnownDefects { get; init; }
    public ChangeDigest LastChangeDigest { get; init; }
    public Dictionary<string, double> ComplexityScores { get; init; }  // 文件级复杂度
}

public record ChangeDigest(string DiffHash, int FilesChanged, int LinesAdded, int LinesDeleted, string Summary);
```

---

## 四、状态机（完整版）

```
                    ┌─────────────────────────────────────┐
                    │                                     │
                    ▼                                     │
┌──────┐    ┌──────┴────┐    ┌─────┐    ┌──────┐    ┌───▼──┐
│ INIT │───▶│   PLAN    │───▶│ DEV │───▶│ TEST │───▶│ DONE │
└──────┘    └──────┬────┘    └──▲──┘    └──┬───┘    └──────┘
                   │            │          │
                   │            └──────────┘ (回流 ≤ maxAttempts)
                   │            │
                   ▼            ▼
              ┌────────┐  ┌──────────────┐
              │ FAILED │  │ PAUSED_FOR   │
              └────────┘  │ _APPROVAL    │
                          └──────────────┘
```

- 非法迁移抛出 `InvalidTransitionException`，记录审计日志。
- 每次迁移触发 `StateTransitioned` 事件。
- 支持自定义状态扩展（`CustomStates` 字典）。

---

## 五、Agent 系统（解耦 + 可扩展）

### 5.1 Agent 注册与发现

```csharp
public interface IAgent
{
    string Name { get; }
    string Version { get; }
    IReadOnlySet<string> Capabilities { get; }  // 声明能力标签
    Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct);
    Task<bool> CanHandleAsync(AgentTask task, CancellationToken ct);  // 动态判断
}

public record AgentContext(
    AgentTask Task,
    ProjectContext Project,
    IFileSystem Workspace,
    IMemoryStore Memory,
    IEventBus Events,
    ILogger Logger,
    CancellationTokenSource TimeoutToken
);
```

- **Agent 插件化**：通过 `IAgentProvider` 动态发现（反射 / DI / 外部程序集加载）。
- **沙箱隔离**：每个 Agent 运行在独立工作区子目录；禁止跨 Agent 文件写入。
- **禁止 Agent 互相直接调用**：所有交互通过 `NextTasks` 入队。

### 5.2 内置 Agent 矩阵

| Agent | 职责 | 输入 | 输出 | 推荐模型 |
|-------|------|------|------|----------|
| **Planner** | 需求拆分、模块设计、文件规划 | 需求文档 | `modules[]`, `file_plan[]`, `steps[]`, `risks[]`, `DoD` | Opus / GPT-4o |
| **Developer** | 代码生成/修改 | 计划产物 | `edits[]: {path, patch, rationale}` | Sonnet / GPT-4o |
| **Tester** | 测试执行与缺陷报告 | 代码变更 | `cases[]`, `commands[]`, `pass`, `failure_signature`, `bugs[]` | Sonnet / GPT-4o |
| **Critic** | 代码审查与改进建议 | 变更集 | `issues[]`, `severity`, `suggestions[]` | Opus / GPT-4o |
| **Reflector** | 元认知：策略评估与调整 | 执行历史 | `strategy_adjustments[]`, `lessons[]` | Opus / GPT-4o |
| **Gate** | 人工审批检查点 | 当前状态 | `approved \| rejected \| changes_requested` | N/A (CLI prompt) |

---

## 六、语义记忆系统

```csharp
public interface IMemoryStore
{
    Task StoreAsync(MemoryEntry entry, CancellationToken ct);
    Task<IReadOnlyList<MemoryEntry>> RecallAsync(string query, int topK, CancellationToken ct);
    Task ForgetAsync(Guid entryId, CancellationToken ct);
    Task CompactAsync(CancellationToken ct);  // 去污染与压缩
}

public record MemoryEntry(
    Guid Id,
    string Content,
    ReadOnlyMemory<float> Embedding,
    MemoryType Type,       // Fact | Decision | Lesson | ErrorPattern
    DateTime CreatedAt,
    DateTime LastAccessedAt,
    int AccessCount,
    double DecayFactor,    // 指数衰减系数
    List<string> Tags
);
```

- **向量存储**：本地轻量（SQLite + sqlite-vec 扩展）或可插拔（Qdrant / Milvus）。
- **去污染策略**：
  - **时效衰减**：`Weight = AccessCount × exp(-DecayFactor × DaysSinceCreation)`。
  - **矛盾检测**：新事实与旧记忆冲突时，标记旧记忆为 `Superseded`。
  - **压缩**：相似记忆合并为摘要（LLM 辅助 summarization）。
  - **禁止**：错误结论作为长期真理（每个记忆带 `Confidence` 字段，低于阈值不参与推理）。

---

## 七、收敛与停机（增强版）

| 机制 | 实现 | 触发条件 |
|------|------|----------|
| **最大轮次** | 全局 `maxIterations`；单阶段 `maxAttempts` | 硬限制触发 → `FAILED` |
| **无进展检测** | 连续 K 次相同 `failure_signature`（标准化哈希） | 签名重复 → `FAILED` + 报告 |
| **Token/Money 预算** | 全局 `maxTokens \|\| maxCost`；单任务 `maxTokensPerTask` | 预算耗尽 → `PAUSED_FOR_APPROVAL` |
| **时间上限** | 全局 `maxWallTime`；单 CLI 超时 `cliTimeoutSeconds` | 超时 → 当前任务 `TIMED_OUT` |
| **质量阈值** | 测试通过率 < `minPassRate` 且无改善趋势 | 连续下降 → `FAILED` |
| **人工闸门** | `PAUSED_FOR_APPROVAL` 状态 + CLI 交互式确认 | 关键决策点自动挂起 |
| **自适应收敛** | 基于历史成功率动态调整 `maxAttempts` 和 `maxIterations` | 高成功率项目放宽，低成功率收紧 |

---

## 八、Tool / CLI 执行沙箱

```csharp
public interface IToolSandbox
{
    Task<ToolResult> ExecuteAsync(ToolInvocation inv, CancellationToken ct);
}

public record ToolInvocation(
    string Command,                  // 可执行文件路径
    string[] Arguments,              // 参数数组（禁止 shell 拼接）
    string WorkingDirectory,
    Dictionary<string, string> Environment,
    TimeSpan Timeout,
    string? StdInput,                // stdin 内容（自动写入临时文件）
    IReadOnlySet<string> AllowedPaths // 文件操作白名单
);

public record ToolResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    TimeSpan WallTime,
    long PeakMemoryBytes
);
```

- **安全保证**：参数数组传递 → 无 shell 注入；文件操作路径必须匹配 `AllowedPaths` 前缀。
- **资源限制**：进程级内存/CPU 限制（Windows Job Objects / Linux cgroups）。
- **网络策略**：默认禁止外网；通过 `Environment` 显式开启。

---

## 九、Prompt 模板（结构化输出契约）

每个 Agent 的 system prompt 必须包含：

```json
{
  "input_constraints": {
    "paths_allowlist": ["src/", "tests/"],
    "readonly_outside_allowlist": true,
    "max_file_size_bytes": 1048576
  },
  "output_schema": {
    "format": "json",
    "schema_ref": "urn:schema:agent-result:v2",
    "extra_fields_policy": "reject",
    "free_text_field": "notes"
  },
  "planner_output": {
    "modules": [{ "name": "", "responsibility": "", "dependencies": [] }],
    "file_plan": [{ "path": "", "action": "create|modify|delete", "rationale": "" }],
    "steps": [{ "order": 0, "description": "", "agent": "dev|test|critique" }],
    "risks": [{ "description": "", "mitigation": "", "severity": "low|medium|high|critical" }],
    "definition_of_done": [""]
  },
  "dev_output": {
    "edits": [{ "path": "", "patch_or_full_file": "", "rationale": "" }]
  },
  "test_output": {
    "cases": [{ "name": "", "input": "", "expected": "" }],
    "executed_commands": [""],
    "pass": true,
    "failure_signature": "",
    "bugs": [{ "description": "", "severity": "", "repro_steps": [""] }]
  }
}
```

- **输出校验**：返回 JSON 必须通过 `JsonSchemaValidator`，校验失败 → 自动重试（带错误反馈），最多 3 次。
- **流式支持**：长输出通过 `IAsyncEnumerable<Token>` 流式返回，避免超时。

---

## 十、LLM 客户端抽象（多提供商）

```csharp
public interface ILLMClient
{
    string ProviderName { get; }
    IReadOnlySet<string> SupportedModels { get; }
    Task<LLMResponse> ExecuteAsync(InvocationSpec spec, CancellationToken ct);
    IAsyncEnumerable<LLMToken> StreamAsync(InvocationSpec spec, CancellationToken ct);
    Task<ModelCapability> GetCapabilityAsync(string modelId, CancellationToken ct);
}

public record InvocationSpec(
    string ModelId,
    string SystemPrompt,
    string UserPrompt,
    int MaxTokens,
    float Temperature,
    JsonSchema? ResponseSchema,       // 结构化输出约束
    Dictionary<string, object>? ProviderOptions
);

public record LLMResponse(
    string Content,
    TokenUsage Usage,
    TimeSpan Duration,
    string ModelId,
    string FinishReason
);
```

- **实现**：`ClaudeCliClient` / `CodexCliClient` / `OpenAIClient`（通过 `HttpClient` + API Key）。
- **语义缓存**：`ISemanticCache` 拦截相似 prompt，命中则直接返回缓存结果（基于 embedding 相似度 > 0.95）。
- **故障转移**：主模型失败 → 按 `[fallbackModel1, fallbackModel2, ...]` 顺序重试。

---

## 十一、可观测性（OpenTelemetry 原生）

```csharp
// 必须埋点
public static class OrchestratorMetrics
{
    public static readonly Counter<int> TasksCompleted;
    public static readonly Histogram<double> TaskDurationSeconds;
    public static readonly Histogram<double> LLMCallDurationSeconds;
    public static readonly Counter<int> LLMTokensConsumed;
    public static readonly Counter<double> LLMCostDollars;
    public static readonly Histogram<int> ConvergenceIterations;
    public static readonly Counter<int> StateTransitionErrors;
}
```

- **追踪**：每个 Task 带 `traceId` + `spanId`，完整链路：`Router → Agent → CLI → Tool`。
- **日志**：结构化 JSON（Serilog），字段：`taskId`, `agentType`, `status`, `durationMs`, `tokenCount`。
- **导出**：OTLP → Jaeger / Grafana / Prometheus（可配置）。
- **健康端点**：`/healthz`（存活）、`/readyz`（就绪）、`/metrics`（Prometheus scrape）。

---

## 十二、持久化与恢复

```csharp
public interface IStateStore
{
    Task<OrchestratorState> LoadAsync(CancellationToken ct);
    Task SaveAsync(OrchestratorState state, CancellationToken ct);
    Task<long> GetVersionAsync(CancellationToken ct);  // 乐观锁
}

public record OrchestratorState(
    long Version,                          // 乐观锁版本号
    List<AgentTask> Queue,                 // 待执行队列
    List<AgentTask> Completed,             // 已完成
    ProjectContext Project,
    ConvergenceState Convergence,
    BudgetState Budget,
    DateTime LastSavedAt
);
```

- **原子写入**：先写临时文件 → 校验 JSON → `File.Move`（原子替换）。
- **可恢复性**：任意崩溃后 `resume` 从 `state.json` 完整恢复，无需重新开始。
- **版本兼容**：状态文件带 `SchemaVersion`，支持向前兼容迁移。

---

## 十三、DAG 并行执行（Phase 2，明确触发条件）

```
         ┌──────┐
         │ PLAN │
         └──┬───┘
     ┌──────┼──────┐
     ▼      ▼      ▼
  ┌────┐ ┌────┐ ┌────┐
  │DEV1│ │DEV2│ │DEV3│    ← 无依赖模块可并行
  └──┬─┘ └──┬─┘ └──┬─┘
     └──────┼──────┘
            ▼
         ┌──────┐
         │ MERGE│           ← 合并检查
         └──┬───┘
            ▼
         ┌──────┐
         │ TEST │
         └──────┘
```

**前置条件**：
- 锁文件机制：并行 Agent 修改不同文件；冲突文件需要 `FileLockManager` 协调。
- 合并策略：优先自动合并（无冲突）；冲突时插入额外 `Dev(Merge)` 任务。
- `DependsOn` 字段必须完整声明依赖关系。

---

## 十四、自检清单（增强版）

### 安全
- [ ] 无 shell 注入：所有 CLI 调用使用参数数组，非字符串拼接。
- [ ] 长 prompt 走 stdin 或临时文件，非巨型命令行参数。
- [ ] 文件操作限制在 `AllowedPaths` 白名单内。
- [ ] 模型输出在写入文件系统前经过路径校验。

### 可靠性
- [ ] 任意崩溃可 `resume` 从 `state.json` 继续。
- [ ] 测试失败回流可解释（附带命令与代码片段）。
- [ ] 状态迁移必须经过校验，非法迁移记录并拒绝。
- [ ] 单写者模型保证队列 + 状态写入安全（`Channel<AgentTask>` 或锁）。

### 可观测
- [ ] 每个 Task 有完整 tracing span。
- [ ] LLM 调用耗时、Token 消耗、成本可追踪。
- [ ] 收敛原因可回溯（轮次/无进展/预算/超时）。
- [ ] 健康检查端点可被外部监控系统探测。

### 智能性
- [ ] 语义记忆写入带衰减因子和置信度。
- [ ] 任务路由基于历史成功率自适应调整。
- [ ] LLM 响应缓存基于语义相似度（非纯文本匹配）。
- [ ] 自适应收敛参数根据项目特征动态调节。

---

## 十五、交付物

| 产物 | 说明 |
|------|------|
| C# CLI | `run` / `resume` / `status` / `dry-run` 命令 |
| `ILLMClient` 实现 | Claude CLI / Codex CLI / OpenAI API（至少两个 + Mock） |
| 集成测试 | 进程录制 fixture / Mock LLMClient + 端到端场景 |
| `samples/` | 最小示例仓库 + 一次完整 dry-run 演示 |
| OpenTelemetry 仪表盘模板 | Grafana dashboard JSON（指标 + 追踪） |
| `README.md` | CLI 路径配置、环境变量、Windows 注意事项、快速开始 |

---

## 十六、技术栈

```
.NET 10 C#           → 编排引擎
System.Threading.Channels → 队列与背压
Serilog + OpenTelemetry → 日志/指标/追踪
SQLite + sqlite-vec  → 本地语义记忆（零依赖）
System.Text.Json     → 配置与状态序列化
Spectre.Console      → CLI 交互界面
xUnit + NSubstitute  → 单元/集成测试
```

---

## 十七、快速开始（最小可运行）

```bash
# 1. 初始化项目
dotnet new console -n AgentOrchestrator
cd AgentOrchestrator

# 2. 配置 CLI 路径
export CLAUDE_CLI_PATH="C:\Program Files\Claude\claude.exe"
export CODEX_CLI_PATH="C:\Program Files\OpenAI\codex.exe"

# 3. 运行编排器
dotnet run -- run --workspace ./samples/demo --max-iterations 10

# 4. 查看状态
dotnet run -- status --workspace ./samples/demo

# 5. 从崩溃恢复
dotnet run -- resume --workspace ./samples/demo
```
