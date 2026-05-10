using AgentOrchestrator.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentOrchestrator.Infrastructure.Persistence;

/// <summary>
/// 本地磁盘文件系统实现，强制所有路径操作限定在 workspace 根目录内，
/// 防止路径遍历攻击（如 ../../etc/passwd）。
/// </summary>
public class WorkspaceFileSystem(string rootPath, ILogger<WorkspaceFileSystem> logger) : IFileSystem
{
    public string RootPath { get; } = Path.GetFullPath(rootPath);

    public async Task<string> ReadAsync(string relativePath, CancellationToken ct)
    {
        var full = ResolveSafe(relativePath);
        return await File.ReadAllTextAsync(full, ct);
    }

    public async Task WriteAsync(string relativePath, string content, CancellationToken ct)
    {
        var full = ResolveSafe(relativePath);
        EnsureParentDirectory(full);

        var tmp = full + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, full, overwrite: true);
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("文件已写入: {Path}", relativePath);
        }
    }

    public async Task WriteBytesAsync(string relativePath, byte[] content, CancellationToken ct)
    {
        var full = ResolveSafe(relativePath);
        EnsureParentDirectory(full);

        var tmp = full + ".tmp";
        await File.WriteAllBytesAsync(tmp, content, ct);
        File.Move(tmp, full, overwrite: true);
    }

    public bool Exists(string relativePath)
    {
        var full = ResolveSafe(relativePath);
        return File.Exists(full) || Directory.Exists(full);
    }

    public void EnsureDirectory(string relativePath)
    {
        var full = ResolveSafe(relativePath);
        Directory.CreateDirectory(full);
    }

    public IEnumerable<string> ListFiles(string relativePath, string pattern = "*")
    {
        var full = ResolveSafe(relativePath);
        if (!Directory.Exists(full))
        {
            return [];
        }

        return Directory.EnumerateFiles(full, pattern, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(RootPath, f));
    }

    public void Delete(string relativePath)
    {
        var full = ResolveSafe(relativePath);
        if (File.Exists(full))
        {
            File.Delete(full);
        }
    }

    public bool IsPathAllowed(string relativePath, IReadOnlySet<string> allowlist)
    {
        // 先解析为绝对路径（消除 ../ 等），再做前缀比较
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        }
        catch
        {
            return false;
        }

        // 路径必须仍在 workspace 根目录内
        if (!IsUnderRoot(fullPath))
        {
            return false;
        }

        return allowlist.Any(prefix =>
        {
            var prefixFull = Path.GetFullPath(Path.Combine(RootPath, prefix.TrimStart('/').TrimStart('\\')));
            // 加路径分隔符防止同名前缀目录绕过
            return fullPath.StartsWith(
                prefixFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, prefixFull, StringComparison.OrdinalIgnoreCase);
        });
    }

    public string ResolveAndValidate(string relativePath, IReadOnlySet<string> allowlist)
    {
        if (!IsPathAllowed(relativePath, allowlist))
        {
            throw new UnauthorizedAccessException($"路径 '{relativePath}' 不在白名单内");
        }

        return ResolveSafe(relativePath);
    }

    private static void EnsureParentDirectory(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>
    /// 解析绝对路径并验证未逃出 workspace 根目录。
    /// 加路径分隔符防止同名前缀目录绕过（/workspace 不匹配 /workspace-evil）。
    /// </summary>
    private string ResolveSafe(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        if (!IsUnderRoot(full))
        {
            throw new UnauthorizedAccessException($"路径遍历攻击检测: '{relativePath}'");
        }

        return full;
    }

    /// <summary>
    /// 判断绝对路径是否在 workspace 根目录内（含根目录本身）。
    /// </summary>
    private bool IsUnderRoot(string fullPath)
    {
        var root = RootPath.TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase);
    }
}
