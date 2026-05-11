using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AgentOrchestrator.Cli;

internal sealed class SpectreConsoleLoggerProvider : ILoggerProvider
{
    private static readonly object ConsoleLock = new();

    public SpectreConsoleLoggerProvider(string workspacePath)
    {
        _ = workspacePath;
    }

    public ILogger CreateLogger(string categoryName) =>
        new SpectreConsoleLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class SpectreConsoleLogger(string categoryName) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Information && categoryName.StartsWith("AgentOrchestrator.", StringComparison.Ordinal);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var rendered = Render(categoryName, logLevel, state, message);
            lock (ConsoleLock)
            {
                AnsiConsole.MarkupLine(rendered);
            }
        }

        private static string Render<TState>(
            string category,
            LogLevel level,
            TState state,
            string message)
        {
            var template = string.Empty;
            var props = new Dictionary<string, object?>(StringComparer.Ordinal);

            if (state is IEnumerable<KeyValuePair<string, object?>> items)
            {
                foreach (var item in items)
                {
                    if (item.Key == "{OriginalFormat}")
                    {
                        template = item.Value?.ToString() ?? string.Empty;
                    }
                    else
                    {
                        props[item.Key] = item.Value;
                    }
                }
            }

            var source = GetSourceLabel(category);
            var text = OptimizeMessage(category, template, props, message);
            var levelTag = level switch
            {
                LogLevel.Warning => "[yellow]WRN[/]",
                LogLevel.Error => "[red]ERR[/]",
                LogLevel.Critical => "[bold red]FTL[/]",
                _ => "[green]INF[/]",
            };

            return $"[grey]{DateTime.Now:HH:mm:ss}[/] {levelTag} [grey]{Markup.Escape(source)}[/] {Markup.Escape(text)}";
        }

        private static string OptimizeMessage(
            string category,
            string template,
            IReadOnlyDictionary<string, object?> props,
            string fallback)
        {
            var optimized = category switch
            {
                var c when c.EndsWith("Memory.SqliteMemoryStore", StringComparison.Ordinal) =>
                    FormatMemory(template, props, fallback),
                var c when c.EndsWith("Sandbox.ProcessToolSandbox", StringComparison.Ordinal) =>
                    FormatSandbox(template, props, fallback),
                var c when c.EndsWith("LLMClients.FallbackLLMClient", StringComparison.Ordinal) =>
                    FormatFallback(template, props, fallback),
                var c when c.EndsWith("LLMClients.ClaudeCliClient", StringComparison.Ordinal) ||
                          c.EndsWith("LLMClients.CodexCliClient", StringComparison.Ordinal) =>
                    FormatCli(category, template, props, fallback),
                var c when c.EndsWith("Orchestration.OrchestratorEngine", StringComparison.Ordinal) =>
                    FormatOrchestrator(template, props, fallback),
                var c when c.EndsWith("Persistence.JsonStateStore", StringComparison.Ordinal) =>
                    FormatStateStore(template, props, fallback),
                var c when c.EndsWith("Routing.IntelligentTaskRouter", StringComparison.Ordinal) =>
                    FormatRouter(template, props, fallback),
                var c when c.EndsWith("EventBus.InMemoryEventBus", StringComparison.Ordinal) =>
                    FormatEventBus(template, props, fallback),
                var c when c.EndsWith("Agents.Base.AgentBase", StringComparison.Ordinal) =>
                    FormatAgent(template, props, fallback),
                _ => fallback,
            };

            return string.IsNullOrWhiteSpace(optimized) ? fallback : optimized;
        }

        private static string FormatMemory(string template, IReadOnlyDictionary<string, object?> props, string fallback)
        {
            if (template.Contains("语义记忆 SQLite 已初始化", StringComparison.Ordinal))
            {
                return $"记忆库已就绪: {GetString(props, "Path", fallback)}";
            }

            if (template.Contains("记忆压缩完成", StringComparison.Ordinal))
            {
                return $"记忆压缩完成，清理 {GetString(props, "Count", "0")} 条低权重记忆";
            }

            return fallback;
        }

        private static string FormatSandbox(string template, IReadOnlyDictionary<string, object?> props, string fallback)
        {
            if (template.Contains("执行工具", StringComparison.Ordinal))
            {
                return $"工具启动: {FormatCommand(GetString(props, "Command", string.Empty), GetString(props, "Args", string.Empty))}";
            }

            if (template.Contains("工具仍在运行", StringComparison.Ordinal))
            {
                return $"工具运行中: {Path.GetFileName(GetString(props, "Command", string.Empty))}，已等待 {GetString(props, "ElapsedSeconds", "?")}s / {GetString(props, "TimeoutSeconds", "?")}s";
            }

            if (template.Contains("工具完成", StringComparison.Ordinal))
            {
                return $"工具完成: exit={GetString(props, "Code", "?")}，耗时 {GetString(props, "Ms", "?")}ms";
            }

            if (template.Contains("工具被取消或超时", StringComparison.Ordinal))
            {
                return $"工具超时，已终止进程 PID={GetString(props, "PID", "?")}";
            }

            return fallback;
        }

        private static string FormatFallback(string template, IReadOnlyDictionary<string, object?> props, string fallback)
        {
            if (template.Contains("LLM 客户端", StringComparison.Ordinal))
            {
                return $"模型调用失败: {GetString(props, "Provider", "?")}/{CompactModel(GetString(props, "Model", "?"))}，{GetString(props, "Error", "未知错误")}";
            }

            if (template.Contains("无客户端支持模型", StringComparison.Ordinal) ||
                template.Contains("LLM 模型", StringComparison.Ordinal))
            {
                return $"模型降级: {CompactModel(GetString(props, "Model", "?"))} -> {GetString(props, "Provider", "?")}/{CompactModel(GetString(props, "FallbackModel", "?"))}";
            }

            return fallback;
        }

        private static string FormatCli(string category, string template, IReadOnlyDictionary<string, object?> props, string fallback)
        {
            var provider = category.EndsWith("ClaudeCliClient", StringComparison.Ordinal)
                ? "Claude CLI"
                : "Codex CLI";
            if (template.Contains("失败", StringComparison.Ordinal))
            {
                return $"{provider} 调用失败: exit={GetString(props, "Code", "?")}，{GetString(props, "Err", "无错误输出")}";
            }

            return fallback;
        }

        private static string FormatOrchestrator(string template, IReadOnlyDictionary<string, object?> props, string fallback)
        {
            if (template.Contains("任务入队", StringComparison.Ordinal))
            {
                return $"任务入队: {ShortId(GetString(props, "TaskId", string.Empty))} {GetString(props, "Type", "?")}，第 {GetString(props, "Attempt", "0")} 次尝试，输入 {GetString(props, "Input", string.Empty)}";
            }

            if (template.Contains("任务状态变更", StringComparison.Ordinal))
            {
                return $"任务状态: {ShortId(GetString(props, "TaskId", string.Empty))} {GetString(props, "From", "?")} → {GetString(props, "To", "?")}（{GetString(props, "Reason", "无原因")}）";
            }

            if (template.Contains("任务路由", StringComparison.Ordinal))
            {
                return $"路由: {ShortId(GetString(props, "TaskId", string.Empty))} {GetString(props, "Type", "?")} -> {GetString(props, "Agent", "?")}/{CompactModel(GetString(props, "Model", "?"))} ({GetString(props, "Confidence", "?")})";
            }

            if (template.Contains("Agent 执行完成", StringComparison.Ordinal))
            {
                return $"Agent 完成: {ShortId(GetString(props, "TaskId", string.Empty))}，结果 {FormatSuccess(GetString(props, "Success", "?"))}，耗时 {GetString(props, "DurationMs", "?")}ms";
            }

            if (template.Contains("编排器启动", StringComparison.Ordinal))
            {
                return $"编排器启动: {GetString(props, "Ref", string.Empty)}{(GetString(props, "ForceNew", "False").Equals("True", StringComparison.OrdinalIgnoreCase) ? "（全新启动）" : string.Empty)}";
            }

            if (template.Contains("上次运行已结束", StringComparison.Ordinal))
            {
                return $"恢复结束: 已完成 {GetString(props, "Done", "0")}，已失败 {GetString(props, "Failed", "0")}，无待恢复任务";
            }

            if (template.Contains("预算超限", StringComparison.Ordinal))
            {
                return "预算超限，暂停编排";
            }

            if (template.Contains("任务超时", StringComparison.Ordinal))
            {
                return $"任务超时: {ShortId(GetString(props, "TaskId", string.Empty))}";
            }

            if (template.Contains("任务执行异常", StringComparison.Ordinal))
            {
                return $"任务执行异常: {ShortId(GetString(props, "TaskId", string.Empty))}";
            }

            if (template.Contains("JSON 校验失败", StringComparison.Ordinal))
            {
                return $"JSON 校验失败: {GetString(props, "A", "?")}/{GetString(props, "Max", "?")}，{GetString(props, "Err", "未知错误")}";
            }

            if (template.Contains("LLM 调用", StringComparison.Ordinal))
            {
                return $"LLM 调用失败: 已重试 {GetString(props, "MaxRetries", "?")} 次";
            }

            if (template.Contains("Developer 试图写入白名单外路径", StringComparison.Ordinal))
            {
                return $"开发者写入被拦截: {GetString(props, "Path", "?")}";
            }

            if (template.Contains("Gate: CI 自动审批通过", StringComparison.Ordinal))
            {
                return "Gate: CI 自动审批通过";
            }

            if (template.Contains("无进展检测触发", StringComparison.Ordinal))
            {
                return $"无进展检测触发: 连续 {GetString(props, "N", "0")} 次相同签名";
            }

            if (template.Contains("任务超过最大重试次数", StringComparison.Ordinal))
            {
                return $"任务超过最大重试次数: {GetString(props, "Max", "0")}";
            }

            if (template.Contains("达到最大迭代轮次", StringComparison.Ordinal))
            {
                return $"达到最大迭代轮次: {GetString(props, "Max", "0")}";
            }

            if (template.Contains("非法状态迁移", StringComparison.Ordinal))
            {
                return $"状态迁移异常: {GetString(props, "From", "?")} → {GetString(props, "To", "?")}";
            }

            if (template.Contains("编排器结束", StringComparison.Ordinal))
            {
                return $"编排器结束: 完成 {GetString(props, "Done", "0")}，失败 {GetString(props, "Failed", "0")}";
            }

            return fallback;
        }

        private static string FormatStateStore(string template, IReadOnlyDictionary<string, object?> props, string fallback)
        {
            if (template.Contains("状态已加载", StringComparison.Ordinal))
            {
                return $"状态已加载: 版本 {GetString(props, "Version", "?")}，队列 {GetString(props, "QueueLen", "?")}";
            }

            if (template.Contains("保存 state.json 失败", StringComparison.Ordinal))
            {
                return "状态保存失败";
            }

            return fallback;
        }

        private static string FormatRouter(string template, IReadOnlyDictionary<string, object?> props, string fallback)
        {
            if (template.Contains("规则路由", StringComparison.Ordinal))
            {
                return $"规则路由: {GetString(props, "Type", "?")} -> {GetString(props, "Agent", "?")}/{GetString(props, "Model", "?")}";
            }

            if (template.Contains("语义路由", StringComparison.Ordinal))
            {
                return $"语义路由: {GetString(props, "Type", "?")} -> {GetString(props, "Agent", "?")}/{GetString(props, "Model", "?")}";
            }

            return fallback;
        }

        private static string FormatEventBus(string template, IReadOnlyDictionary<string, object?> props, string fallback)
        {
            if (template.Contains("事件处理器异常", StringComparison.Ordinal))
            {
                return $"事件处理器异常: {GetString(props, "EventType", "?")}";
            }

            return fallback;
        }

        private static string FormatAgent(string template, IReadOnlyDictionary<string, object?> props, string fallback)
        {
            if (template.Contains("JSON 校验失败", StringComparison.Ordinal))
            {
                return $"JSON 校验失败: {GetString(props, "A", "?")}/{GetString(props, "Max", "?")}，{GetString(props, "Err", "未知错误")}";
            }

            if (template.Contains("LLM 调用", StringComparison.Ordinal))
            {
                return $"LLM 调用失败: 已重试 {GetString(props, "MaxRetries", "?")} 次";
            }

            if (template.Contains("Developer 试图写入白名单外路径", StringComparison.Ordinal))
            {
                return $"开发者写入被拦截: {GetString(props, "Path", "?")}";
            }

            if (template.Contains("Gate: CI 自动审批通过", StringComparison.Ordinal))
            {
                return "Gate: CI 自动审批通过";
            }

            return fallback;
        }

        private static string GetSourceLabel(string category)
        {
            return category switch
            {
                var c when c.EndsWith("Orchestration.OrchestratorEngine", StringComparison.Ordinal) => "编排",
                var c when c.EndsWith("Sandbox.ProcessToolSandbox", StringComparison.Ordinal) => "工具沙箱",
                var c when c.EndsWith("LLMClients.ClaudeCliClient", StringComparison.Ordinal) => "Claude CLI",
                var c when c.EndsWith("LLMClients.CodexCliClient", StringComparison.Ordinal) => "Codex CLI",
                var c when c.EndsWith("LLMClients.FallbackLLMClient", StringComparison.Ordinal) => "LLM 路由",
                var c when c.EndsWith("Memory.SqliteMemoryStore", StringComparison.Ordinal) => "记忆",
                var c when c.EndsWith("Persistence.JsonStateStore", StringComparison.Ordinal) => "状态",
                var c when c.EndsWith("Routing.IntelligentTaskRouter", StringComparison.Ordinal) => "路由",
                var c when c.EndsWith("EventBus.InMemoryEventBus", StringComparison.Ordinal) => "事件总线",
                var c when c.EndsWith("Agents.Base.AgentBase", StringComparison.Ordinal) => "Agent",
                _ => category.Split('.').LastOrDefault() ?? category,
            };
        }

        private static string FormatCommand(string command, string args)
        {
            var name = Path.GetFileName(command);
            if (string.IsNullOrWhiteSpace(args))
            {
                return name;
            }

            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var model = TryGetArgValue(parts, "--model") ?? TryGetArgValue(parts, "-m");
            if (!string.IsNullOrWhiteSpace(model))
            {
                return $"{name} model={CompactModel(model)}";
            }

            var compact = string.Join(' ', parts);
            if (compact.Length > 120)
            {
                compact = compact[..120] + "...";
            }

            return $"{name} {compact}";
        }

        private static string? TryGetArgValue(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        private static string ShortId(string id) =>
            Guid.TryParse(id, out var guid) ? guid.ToString("N")[..8] : id;

        private static string FormatSuccess(string success) =>
            success.Equals("True", StringComparison.OrdinalIgnoreCase) ? "成功" : "失败";

        private static string CompactModel(string model) =>
            model
                .Replace("claude-", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("-4-5", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("-latest", string.Empty, StringComparison.OrdinalIgnoreCase);

        private static string GetString(IReadOnlyDictionary<string, object?> props, string key, string fallback)
        {
            if (!props.TryGetValue(key, out var value) || value is null)
            {
                return fallback;
            }

            return value switch
            {
                DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
                TimeSpan ts => ts.ToString(),
                bool b => b ? "True" : "False",
                _ => value.ToString() ?? fallback,
            };
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }
}
