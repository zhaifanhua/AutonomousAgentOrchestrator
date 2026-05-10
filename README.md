# Autonomous Agent Orchestrator

事件驱动、智能路由、语义记忆、全链路可观测的 AI Agent 编排引擎（.NET 10 C#）。

## 快速开始

### 前置条件

- .NET 10 SDK
- （可选）Claude CLI：`CLAUDE_CLI_PATH` 环境变量
- （可选）Codex CLI：`CODEX_CLI_PATH` 环境变量

### 环境变量配置

```powershell
# Windows PowerShell
$env:CLAUDE_CLI_PATH = "C:\Program Files\Claude\claude.exe"
$env:CODEX_CLI_PATH  = "C:\Program Files\OpenAI\codex.exe"

# 人工审批自动通过（CI 环境）
$env:GATE_AUTO_APPROVE = "true"
```

### 运行命令

```bash
# 1. 构建
dotnet build

# 2. dry-run（Mock LLM，不消耗真实 Token）
dotnet run --project src/AgentOrchestrator.Cli -- dry-run \
  --workspace samples/demo \
  --requirement requirements.md

# 3. 正式运行
dotnet run --project src/AgentOrchestrator.Cli -- run \
  --workspace samples/demo \
  --requirement requirements.md \
  --max-iterations 10

# 4. 查看状态
dotnet run --project src/AgentOrchestrator.Cli -- status \
  --workspace samples/demo

# 5. 崩溃恢复
dotnet run --project src/AgentOrchestrator.Cli -- resume \
  --workspace samples/demo
```

### 运行测试

```bash
dotnet test
```

## 架构概览

```
Input Queue → Intelligent Router → Agent Executor → Result Handler
                    ↓                    ↓                ↓
             Semantic Memory      Tool/CLI Sandbox   State Manager
```

### 内置 Agent

| Agent | 任务类型 | 推荐模型 |
|-------|---------|---------|
| Planner | `plan` | claude-opus-4-5 |
| Developer | `dev` | claude-sonnet-4-5 |
| Tester | `test` | claude-sonnet-4-5 |
| Critic | `critique` | claude-opus-4-5 |
| Reflector | `reflect` | claude-opus-4-5 |
| Gate | `gate` | CLI 交互 |

## 状态文件

编排器将完整状态持久化到 `<workspace>/state.json`，支持任意崩溃后通过 `resume` 命令恢复。

## 可观测性

- **日志**：`<workspace>/logs/orchestrator.jsonl`（Serilog 结构化 JSON）
- **指标**：OpenTelemetry → OTLP（配置 `OTLP_ENDPOINT` 环境变量）
- **Grafana 仪表盘**：导入 `grafana/dashboard.json`

## 项目结构

```
src/
  AgentOrchestrator.Core/          # 接口、领域模型、状态机
  AgentOrchestrator.Infrastructure/ # 事件总线、持久化、LLM 客户端、记忆
  AgentOrchestrator.Agents/        # 内置 Agent 实现
  AgentOrchestrator.Cli/           # CLI 入口（Spectre.Console）
tests/
  AgentOrchestrator.Tests/         # 单元/集成测试
samples/
  demo/                            # 最小示例（Todo API 需求）
grafana/
  dashboard.json                   # Grafana 仪表盘模板
```

## 安全说明

- 所有 CLI 调用使用参数数组，**禁止 shell 字符串拼接**
- 长 prompt 通过 stdin 传递，不作为命令行参数
- 所有文件操作限制在 `PathsAllowlist` 白名单内
- 路径遍历攻击检测（`../../` 路径注入）
