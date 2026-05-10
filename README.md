# Autonomous Agent Orchestrator

事件驱动、智能路由、语义记忆、全链路可观测的 AI Agent 编排引擎（.NET 10 C#）。

---

## 快速开始

### 前置条件

- .NET 10 SDK
- Claude CLI（`npm i -g @anthropic-ai/claude-code`）或 Codex CLI（`npm i -g @openai/codex`）
  - 已登录 CLI 账号即可，**无需手动配置路径**，程序自动从 `PATH` 探测
  - 两个 CLI 均未安装时自动降级为 Mock 模式

### 安装为全局命令

```powershell
# 打包
dotnet pack src/AgentOrchestrator.Cli -c Release -o ./nupkg

# 安装（已安装先卸载）
dotnet tool uninstall -g AgentOrchestrator.Cli 2>$null
dotnet tool install  -g AgentOrchestrator.Cli --add-source ./nupkg

# 验证
aao --version
```

### 命令参考

```powershell
# 从头启动任务
aao run --workspace ./myproject --requirement requirements.md

# 查看当前状态
aao status --workspace ./myproject

# 崩溃后恢复
aao resume --workspace ./myproject

# Dry-run（Mock LLM，不消耗真实 Token）
aao dry-run --workspace ./myproject
```

**常用选项**

| 选项 | 说明 | 默认值 |
|------|------|--------|
| `-w / --workspace` | 工作目录路径 | `./workspace` |
| `-r / --requirement` | 需求文件路径（相对 workspace） | `requirements.md` |
| `--max-iterations` | 最大迭代轮次 | `20` |
| `--max-attempts` | 单任务最大重试次数 | `3` |
| `--max-cost` | 最大成本上限（美元） | `10.0` |

### 不安装直接运行

```powershell
# 先构建发布版
dotnet publish src/AgentOrchestrator.Cli -c Release -r win-x64 --self-contained

cd src/AgentOrchestrator.Cli/bin/Release/net10.0/publish/win-x64

.\AgentOrchestrator.Cli.exe run --workspace samples/demo --requirement requirements.md
```

### 运行测试

```powershell
dotnet test
```

---

## LLM 配置

编排器按以下优先级选择 LLM 客户端，失败时自动降级：

```
Claude CLI  →  Codex CLI  →  Mock（兜底）
```

**Claude CLI**（推荐）

```powershell
# 安装并登录
npm i -g @anthropic-ai/claude-code
claude login

# 验证
claude --version
```

**Codex CLI**

```powershell
# 安装并登录
npm i -g @openai/codex
codex login

# 验证
codex --version
```

**环境变量覆盖**（可选，用于非标准安装路径）

```powershell
$env:CLAUDE_CLI_PATH = "C:\custom\path\claude.cmd"
$env:CODEX_CLI_PATH  = "C:\custom\path\codex.cmd"

# CI 环境跳过人工审批
$env:GATE_AUTO_APPROVE = "true"
```

---

## 架构概览

```
Input Queue → Intelligent Router → Agent Executor → Result Handler
                    ↓                    ↓                ↓
             Semantic Memory      Tool/CLI Sandbox   State Manager
```

### 内置 Agent

| Agent | 任务类型 | 职责 | 推荐模型 |
|-------|---------|------|---------|
| Planner | `plan` | 需求拆分、模块设计、文件规划 | claude-opus-4-5 |
| Developer | `dev` | 代码生成（完整文件内容） | claude-sonnet-4-5 |
| Tester | `test` | 测试执行与缺陷报告 | claude-sonnet-4-5 |
| Critic | `critique` | 代码审查与改进建议 | claude-opus-4-5 |
| Reflector | `reflect` | 元认知策略评估（多次失败后自动插入） | claude-opus-4-5 |
| Gate | `gate` | 人工审批检查点 | — |

### 收敛机制

| 机制 | 触发条件 |
|------|---------|
| 最大轮次 | `CurrentIteration ≥ MaxIterations` → `FAILED` |
| 无进展检测 | 连续 3 次相同失败签名 → `FAILED` |
| Token/Cost 预算 | 超限 → `PAUSED_FOR_APPROVAL` |
| 单任务超时 | 默认 600 秒 → 重试或 `FAILED` |
| 依赖死锁防护 | 依赖未满足跳过 20 次 → `FAILED` |

---

## 状态与恢复

编排器将完整状态持久化到 `<workspace>/state.json`（原子写入，乐观锁版本号）。
任意崩溃后执行 `aao resume` 从断点继续，无需重新开始。

**workspace 目录结构**

```
<workspace>/
  requirements.md      # 需求文件（输入）
  state.json           # 编排器状态（自动维护）
  memory.db            # 语义记忆 SQLite
  plans/               # Planner 输出的规划文件
  reports/             # Critic 审查报告
  src/                 # Developer 生成的代码
  tests/               # Developer 生成的测试
  logs/
    orchestrator.jsonl # 结构化日志
```

---

## 可观测性

- **日志**：`<workspace>/logs/orchestrator.jsonl`（Serilog 结构化 JSON）
- **指标**：OpenTelemetry，配置 `OTLP_ENDPOINT` 导出到 Jaeger / Prometheus
- **Grafana 仪表盘**：导入 `grafana/dashboard.json`

---

## 项目结构

```
src/
  AgentOrchestrator.Core/           # 接口、领域模型、状态机、指标定义
  AgentOrchestrator.Infrastructure/ # 事件总线、持久化、LLM 客户端、记忆、路由
  AgentOrchestrator.Agents/         # 内置 Agent 实现
  AgentOrchestrator.Cli/            # CLI 入口（Spectre.Console，工具名 aao）
tests/
  AgentOrchestrator.Tests/          # 单元 + 集成测试
samples/
  demo/                             # 最小示例（Todo API 需求）
grafana/
  dashboard.json                    # Grafana 仪表盘模板
```

---

## 安全说明

- CLI 调用使用参数数组，**禁止 shell 字符串拼接**
- 长 prompt 通过 stdin 传递，不作为命令行参数
- 文件操作限制在 `PathsAllowlist` 白名单内，`../ ` 路径遍历自动拦截
- Developer Agent 只接受完整文件内容，拒绝写入 unified diff 格式
