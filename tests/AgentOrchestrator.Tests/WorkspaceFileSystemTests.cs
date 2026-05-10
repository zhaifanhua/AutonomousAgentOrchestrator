using AgentOrchestrator.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentOrchestrator.Tests;

public class WorkspaceFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}");
    private readonly WorkspaceFileSystem _fs;

    public WorkspaceFileSystemTests()
    {
        Directory.CreateDirectory(_root);
        _fs = new WorkspaceFileSystem(_root, NullLogger<WorkspaceFileSystem>.Instance);
    }

    [Fact]
    public async Task WriteAndRead_ShouldRoundTrip()
    {
        await _fs.WriteAsync("src/hello.txt", "Hello World", CancellationToken.None);
        var content = await _fs.ReadAsync("src/hello.txt", CancellationToken.None);
        Assert.Equal("Hello World", content);
    }

    [Fact]
    public async Task PathTraversal_ShouldThrow()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _fs.ReadAsync("../../etc/passwd", CancellationToken.None));
    }

    [Fact]
    public void IsPathAllowed_ShouldRespectAllowlist()
    {
        var allowlist = new HashSet<string> { "src/", "tests/" };
        Assert.True(_fs.IsPathAllowed("src/Core.cs", allowlist));
        Assert.True(_fs.IsPathAllowed("tests/Test.cs", allowlist));
        Assert.False(_fs.IsPathAllowed("secrets/key.pem", allowlist));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
