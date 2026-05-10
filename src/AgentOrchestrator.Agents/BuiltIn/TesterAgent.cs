using AgentOrchestrator.Agents.Base;
using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;

namespace AgentOrchestrator.Agents.BuiltIn;

/// <summary>
/// Tester Agent：执行测试命令并生成缺陷报告。
/// 实际执行通过 IToolSandbox 完成，Agent 负责解析结果和生成诊断。
/// </summary>
public class TesterAgent(ILLMClient llmClient, IToolSandbox sandbox) : AgentBase(llmClient)
{
    public override string Name => "Tester";
    public override string Version => "1.0";
    public override IReadOnlySet<string> Capabilities => new HashSet<string> { "test", "verify" };

    private const string SystemPrompt = """
        你是一个测试工程师。根据代码变更，生成测试计划并分析测试结果，输出严格 JSON：
        {
          "cases": [{"name": "", "input": "", "expected": ""}],
          "executed_commands": ["dotnet test"],
          "pass": true,
          "failure_signature": "",
          "bugs": [{"description": "", "severity": "low|medium|high|critical", "repro_steps": [""]}],
          "notes": ""
        }
        """;

    public override async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        // 先通过 LLM 生成测试计划
        var codeContext = await LoadCodeContextAsync(ctx, ct);
        var spec = new InvocationSpec(
            ModelId: ctx.Task.Tags.GetValueOrDefault("modelId", "claude-sonnet-4-5"),
            SystemPrompt: SystemPrompt,
            UserPrompt: $"代码变更摘要：\n{codeContext}",
            MaxTokens: 2048);

        var plan = await CallLLMWithSchemaAsync<TesterOutput>(spec, ctx, ct);
        if (plan == null)
            return AgentResult.Fail("Tester 未能生成测试计划");

        // 实际执行测试命令
        var testResult = await RunTestCommandsAsync(plan.ExecutedCommands ?? ["dotnet test"], ctx, ct);

        // 分析测试结果
        var passed = testResult.ExitCode == 0;
        var signature = passed ? string.Empty : ComputeFailureSignature(testResult.StdErr, testResult.StdOut);

        if (!passed)
        {
            // 将错误模式写入记忆，供 Developer Agent 下次规避
            await ctx.Memory.StoreAsync(new MemoryEntry
            {
                Content = $"测试失败模式: {testResult.StdErrSnippet(300)}",
                Type = MemoryType.ErrorPattern,
                Tags = ["test", "error", signature],
                RelatedTaskId = ctx.Task.Id,
                Confidence = 0.85
            }, ct);

            // 回流到 Developer（次数由 Orchestrator 层控制）
            var devTask = new AgentTask
            {
                Type = "dev",
                InputRef = ctx.Task.InputRef,
                ParentTaskId = ctx.Task.ParentTaskId,
                Tags = new Dictionary<string, string>(ctx.Task.Tags)
                {
                    ["failureSignature"] = signature,
                    ["testOutput"] = testResult.StdErrSnippet(500)
                }
            };
            return AgentResult.Fail($"测试失败: {testResult.StdErrSnippet(200)}", signature,
                new Diagnostics { ExitCode = testResult.ExitCode, StdErrSnippet = testResult.StdErrSnippet() })
                with
            { NextTasks = [devTask] };
        }

        return AgentResult.Succeed("所有测试通过")
            with
        {
            Diagnostics = new Diagnostics
            {
                ExitCode = 0,
                StdOutSnippet = testResult.StdOutSnippet(500)
            }
        };
    }

    private async Task<string> LoadCodeContextAsync(AgentContext ctx, CancellationToken ct)
    {
        try
        {
            return ctx.Workspace.Exists(ctx.Task.InputRef)
                ? await ctx.Workspace.ReadAsync(ctx.Task.InputRef, ct)
                : ctx.Task.Tags.GetValueOrDefault("stepDesc", "代码变更");
        }
        catch { return "代码变更（无法读取）"; }
    }

    private async Task<ToolResult> RunTestCommandsAsync(
        string[] commands, AgentContext ctx, CancellationToken ct)
    {
        foreach (var cmd in commands)
        {
            var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var exe = parts[0];
            var args = parts.Length > 1 ? parts[1].Split(' ') : [];

            var inv = new ToolInvocation(
                Command: exe,
                Arguments: args,
                WorkingDirectory: ctx.Workspace.RootPath,
                Environment: new Dictionary<string, string>(),
                Timeout: TimeSpan.FromMinutes(5),
                StdInput: null,
                AllowedPaths: ctx.Project.PathsAllowlist.ToHashSet());

            var result = await sandbox.ExecuteAsync(inv, ct);
            if (result.ExitCode != 0) return result;
        }
        return new ToolResult(0, "所有测试命令成功", string.Empty, TimeSpan.Zero, 0);
    }

    private record TesterOutput(
        TestCase[]? Cases,
        string[]? ExecutedCommands,
        bool Pass,
        string? FailureSignature,
        Bug[]? Bugs,
        string? Notes);

    private record TestCase(string Name, string Input, string Expected);
    private record Bug(string Description, string Severity, string[] ReproSteps);
}