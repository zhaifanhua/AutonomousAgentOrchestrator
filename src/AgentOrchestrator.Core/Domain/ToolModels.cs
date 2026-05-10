namespace AgentOrchestrator.Core.Domain;

/// <summary>
/// CLI / 工具调用规格（参数数组方式，禁止 shell 拼接）
/// </summary>
public record ToolInvocation(
    string Command,
    string[] Arguments,
    string WorkingDirectory,
    Dictionary<string, string> Environment,
    TimeSpan Timeout,
    /// <summary>
    /// stdin 内容（超长 prompt 走此通道而非命令行参数）
    /// </summary>
    string? StdInput,
    IReadOnlySet<string> AllowedPaths);

/// <summary>
/// 工具调用结果
/// </summary>
public record ToolResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    TimeSpan WallTime,
    long PeakMemoryBytes)
{
    public bool IsSuccess => ExitCode == 0;

    /// <summary>
    /// 截取 StdOut 摘要（避免超大输出）
    /// </summary>
    public string StdOutSnippet(int maxChars = 2000) =>
        StdOut.Length <= maxChars ? StdOut : StdOut[..maxChars] + "...[truncated]";

    /// <summary>
    /// 截取 StdErr 摘要
    /// </summary>
    public string StdErrSnippet(int maxChars = 1000) =>
        StdErr.Length <= maxChars ? StdErr : StdErr[..maxChars] + "...[truncated]";
}
