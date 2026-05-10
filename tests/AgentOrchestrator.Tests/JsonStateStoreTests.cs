using AgentOrchestrator.Core.Domain;
using AgentOrchestrator.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentOrchestrator.Tests;

public class JsonStateStoreTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}");

    public JsonStateStoreTests() => Directory.CreateDirectory(_tmpDir);

    [Fact]
    public async Task SaveAndLoad_ShouldRoundTrip()
    {
        var store = new JsonStateStore(_tmpDir, NullLogger<JsonStateStore>.Instance);
        var state = new OrchestratorState
        {
            Version = 42,
            Queue = [new AgentTask { Type = "plan", InputRef = "req.md" }],
            Project = new ProjectContext { RequirementSummary = "测试需求" }
        };

        await store.SaveAsync(state, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(42, loaded.Version);
        Assert.Single(loaded.Queue);
        Assert.Equal("测试需求", loaded.Project.RequirementSummary);
    }

    [Fact]
    public async Task Load_WhenFileNotExists_ShouldReturnNull()
    {
        var store = new JsonStateStore(_tmpDir + "/empty", NullLogger<JsonStateStore>.Instance);
        Directory.CreateDirectory(_tmpDir + "/empty");
        var result = await store.LoadAsync(CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetVersion_ShouldReturnCorrectVersion()
    {
        var store = new JsonStateStore(_tmpDir, NullLogger<JsonStateStore>.Instance);
        await store.SaveAsync(new OrchestratorState { Version = 7 }, CancellationToken.None);
        var version = await store.GetVersionAsync(CancellationToken.None);
        Assert.Equal(7, version);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }
}