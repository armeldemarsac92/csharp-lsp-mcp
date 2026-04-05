using CSharpLspMcp.Storage.Graph;
using Xunit;

namespace CSharpLspMcp.Tests;

public class CSharpGraphCacheStoreTests
{
    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsSnapshotAsync()
    {
        var store = new CSharpGraphCacheStore();
        var workspacePath = Path.Combine(Path.GetTempPath(), $"graph-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);

        var snapshot = new WorkspaceGraphSnapshot(
            SchemaVersion: WorkspaceGraphSchema.CurrentVersion,
            WorkspaceRoot: workspacePath,
            WorkspaceTargetPath: Path.Combine(workspacePath, "App.csproj"),
            BuiltAtUtc: DateTimeOffset.UtcNow,
            BuilderVersion: "test",
            BuildMode: "full",
            IncludeTests: true,
            IncludeGenerated: false,
            ProjectsIndexed: 1,
            DocumentsIndexed: 2,
            SymbolsIndexed: 3,
            EdgesIndexed: 4,
            NodeCounts: [new WorkspaceGraphCountItem(WorkspaceGraphNodeKinds.Project, 1)],
            EdgeCounts: [new WorkspaceGraphCountItem(WorkspaceGraphEdgeKinds.Contains, 1)],
            Projects: [new WorkspaceGraphProjectSummary("project:app", "App", "App.csproj", "App", ["NET8_0"], false, 2, 3, 0)],
            ProjectStates: [new WorkspaceGraphProjectState("project:app", "App", "App.csproj", "fingerprint", [])],
            Nodes: [new WorkspaceGraphNode("project:app", WorkspaceGraphNodeKinds.Project, "App", "App", "project:app", null, null, null, null, "App")],
            Edges: [new WorkspaceGraphEdge(WorkspaceGraphEdgeKinds.Contains, "solution:/tmp", "project:app")],
            Features: [WorkspaceGraphEdgeKinds.Calls],
            Warnings: []);

        try
        {
            await store.SaveAsync(snapshot, CancellationToken.None);

            var loaded = await store.LoadAsync(workspacePath, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(snapshot.WorkspaceRoot, loaded.WorkspaceRoot);
            Assert.Equal(snapshot.ProjectsIndexed, loaded.ProjectsIndexed);
            Assert.Single(loaded.NodeCounts);
            Assert.Single(loaded.Projects);
        }
        finally
        {
            var storagePath = store.GetStoragePath(workspacePath);
            if (File.Exists(storagePath))
                File.Delete(storagePath);

            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
