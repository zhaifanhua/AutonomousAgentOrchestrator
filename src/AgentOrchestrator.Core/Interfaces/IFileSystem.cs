namespace AgentOrchestrator.Core.Interfaces;

/// <summary>
/// 文件系统抽象：强制路径白名单校验，防止越权写入
/// </summary>
public interface IFileSystem
{
    string RootPath { get; }

    /// <summary>
    /// 读取文件内容（路径相对于 workspace 根）
    /// </summary>
    Task<string> ReadAsync(string relativePath, CancellationToken ct);

    /// <summary>
    /// 原子写入文件（临时文件 → 校验 → Move）
    /// </summary>
    Task WriteAsync(string relativePath, string content, CancellationToken ct);

    /// <summary>
    /// 写入二进制文件
    /// </summary>
    Task WriteBytesAsync(string relativePath, byte[] content, CancellationToken ct);

    bool Exists(string relativePath);

    void EnsureDirectory(string relativePath);

    IEnumerable<string> ListFiles(string relativePath, string pattern = "*");

    void Delete(string relativePath);

    /// <summary>
    /// 校验路径是否在白名单内（防止路径遍历攻击）
    /// </summary>
    bool IsPathAllowed(string relativePath, IReadOnlySet<string> allowlist);

    /// <summary>
    /// 解析为绝对路径并校验安全性
    /// </summary>
    string ResolveAndValidate(string relativePath, IReadOnlySet<string> allowlist);
}