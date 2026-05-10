using AgentOrchestrator.Agents.Base;
using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentOrchestrator.Agents.BuiltIn;

/// <summary>
/// Developer Agent：根据规划产物生成或修改代码。
/// 输出 edits 数组，每项包含 path / patch_or_full_file / rationale。
/// </summary>
public class DeveloperAgent(ILLMClient llmClient) : AgentBase(llmClient)
{
    private const string SystemPrompt = """
        你是一个代码实现工程师。根据规划文档，输出严格的 JSON（不得有 markdown 包裹）：
        {
          "edits": [
            {"path": "相对路径", "patch_or_full_file": "完整文件内容或 unified diff", "rationale": "修改原因"}
          ],
          "notes": ""
        }
        确保所有路径均在 src/ 或 tests/ 目录内。禁止输出 workspace 根目录外的路径。
        """;

    public override string Name => "Developer";
    public override string Version => "1.0";
    public override IReadOnlySet<string> Capabilities => new HashSet<string> { "dev", "implement" };

    public override async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var planJson = ctx.Workspace.Exists(ctx.Task.InputRef)
            ? await ctx.Workspace.ReadAsync(ctx.Task.InputRef, ct)
            : ctx.Task.InputRef;

        // 检索相关错误模式，避免重蹈覆辙
        var errorPatterns = await ctx.Memory.RecallAsync("error bug fix", topK: 3, ct);
        var avoidance = errorPatterns.Count > 0
            ? "\n\n避免以下历史错误模式：\n" + string.Join("\n", errorPatterns.Select(m => $"- {m.Content}"))
            : string.Empty;

        var spec = new InvocationSpec(
            ModelId: ctx.Task.Tags.GetValueOrDefault("modelId", "claude-sonnet-4-5"),
            SystemPrompt: SystemPrompt,
            UserPrompt: $"规划文档：\n{planJson}\n步骤描述：{ctx.Task.Tags.GetValueOrDefault("stepDesc", "实现代码")}{avoidance}",
            MaxTokens: 8192);

        var output = await CallLLMWithSchemaAsync<DeveloperOutput>(spec, ctx, ct);
        if (output?.Edits == null || output.Edits.Length == 0)
        {
            return AgentResult.Fail("Developer 未能生成代码编辑");
        }

        // 写入代码文件（路径校验在 IFileSystem 实现中）
        var artifacts = new List<Artifact>();
        var allowlist = new HashSet<string>(ctx.Project.PathsAllowlist.DefaultIfEmpty("src/"));

        foreach (var edit in output.Edits)
        {
            if (!ctx.Workspace.IsPathAllowed(edit.Path, allowlist))
            {
                ctx.Logger.LogWarning("Developer 试图写入白名单外路径: {Path}，已忽略", edit.Path);
                continue;
            }
            await ctx.Workspace.WriteAsync(edit.Path, edit.PatchOrFullFile, ct);
            artifacts.Add(new Artifact(edit.Path, "code", string.Empty, 0, DateTime.UtcNow));
        }

        // 生成测试任务
        var testTask = new AgentTask
        {
            Type = "test",
            InputRef = ctx.Task.InputRef,
            ParentTaskId = ctx.Task.ParentTaskId,
            Tags = new Dictionary<string, string>(ctx.Task.Tags) { ["previousDevTaskId"] = ctx.Task.Id.ToString() }
        };

        return AgentResult.Succeed($"代码生成完成: {artifacts.Count} 个文件", [testTask])
            with
        { Artifacts = artifacts };
    }

    private record DeveloperOutput(Edit[]? Edits, string? Notes);
    private record Edit(string Path, string PatchOrFullFile, string Rationale);
}
