using AgentOrchestrator.Core.Domain;

namespace AgentOrchestrator.Core.Interfaces;

/// <summary>
/// 工具/CLI 执行沙箱：安全执行外部进程，实施路径白名单和资源限制
/// </summary>
public interface IToolSandbox
{
    Task<ToolResult> ExecuteAsync(ToolInvocation inv, CancellationToken ct);
}