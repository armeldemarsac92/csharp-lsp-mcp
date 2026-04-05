using CSharpLspMcp.Analysis.Graph;
using CSharpLspMcp.Storage.Graph;
using CSharpLspMcp.Workspace;
using CSharpLspMcp.Workspace.Roslyn;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CSharpLspMcp.Tests;

public class CSharpGraphProjectionServiceTests
{
    [Fact]
    public async Task ExportAsync_RendersMermaidOverviewAsync()
    {
        var workspacePath = TestWorkspaceFactory.CreateChangeImpactWorkspace();
        var graphStore = new CSharpGraphCacheStore();
        try
        {
            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var buildService = new CSharpGraphBuildService(
                new CSharpRoslynWorkspaceHost(NullLogger<CSharpRoslynWorkspaceHost>.Instance),
                graphStore,
                workspaceState);
            await buildService.BuildAsync(
                path: workspacePath,
                mode: "full",
                includeTests: true,
                includeGenerated: false,
                CancellationToken.None);

            var projectionService = new CSharpGraphProjectionService(buildService, graphStore, workspaceState);
            var result = await projectionService.ExportAsync(
                path: workspacePath,
                layout: "mermaid",
                focusSymbol: null,
                projectFilter: null,
                includeTypes: false,
                includeMembers: false,
                includeDocuments: false,
                includeDi: true,
                includeEntrypoints: true,
                edgeKinds: null,
                maxNodes: 40,
                maxEdges: 80,
                rebuildIfMissing: false,
                writeToFile: true,
                outputPath: null,
                CancellationToken.None);

            Assert.Equal("mermaid", result.Layout);
            Assert.Contains("flowchart LR", result.Projection, StringComparison.Ordinal);
            Assert.True(result.WriteToFile);
            Assert.False(string.IsNullOrWhiteSpace(result.ExportPath));
            Assert.EndsWith(".mmd", result.ExportPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(workspacePath, result.ExportPath)));
            Assert.Contains(result.Nodes, node => node.Kind == WorkspaceGraphNodeKinds.Project && node.Label.Contains("Sample.App", StringComparison.Ordinal));
            Assert.Contains(result.Nodes, node => node.Kind == WorkspaceGraphNodeKinds.DiRegistration);
            Assert.Contains(result.Edges, edge => edge.Kind == WorkspaceGraphEdgeKinds.DependsOnProject);
        }
        finally
        {
            var storagePath = graphStore.GetStoragePath(workspacePath);
            if (File.Exists(storagePath))
                File.Delete(storagePath);

            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_RendersFocusedNeighborhoodWithRegistrationAsync()
    {
        var workspacePath = TestWorkspaceFactory.CreateChangeImpactWorkspace();
        var graphStore = new CSharpGraphCacheStore();
        try
        {
            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var buildService = new CSharpGraphBuildService(
                new CSharpRoslynWorkspaceHost(NullLogger<CSharpRoslynWorkspaceHost>.Instance),
                graphStore,
                workspaceState);
            await buildService.BuildAsync(
                path: workspacePath,
                mode: "full",
                includeTests: true,
                includeGenerated: false,
                CancellationToken.None);

            var projectionService = new CSharpGraphProjectionService(buildService, graphStore, workspaceState);
            var result = await projectionService.ExportAsync(
                path: workspacePath,
                layout: "mermaid",
                focusSymbol: "Sample.Core.IWeatherService",
                projectFilter: null,
                includeTypes: false,
                includeMembers: false,
                includeDocuments: false,
                includeDi: true,
                includeEntrypoints: true,
                edgeKinds: null,
                maxNodes: 20,
                maxEdges: 40,
                rebuildIfMissing: false,
                writeToFile: false,
                outputPath: null,
                CancellationToken.None);

            Assert.Equal("focus", result.Mode);
            Assert.Contains(result.Nodes, node => node.Kind == WorkspaceGraphNodeKinds.Type && node.Label.Contains("IWeatherService", StringComparison.Ordinal));
            Assert.Contains(result.Nodes, node => node.Kind == WorkspaceGraphNodeKinds.Type && node.Label.Contains("WeatherService", StringComparison.Ordinal));
            Assert.Contains(result.Nodes, node => node.Kind == WorkspaceGraphNodeKinds.DiRegistration);
            Assert.Contains(result.Edges, edge => edge.Kind == WorkspaceGraphEdgeKinds.RegisteredAs);
        }
        finally
        {
            var storagePath = graphStore.GetStoragePath(workspacePath);
            if (File.Exists(storagePath))
                File.Delete(storagePath);

            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_RendersDotProjectSliceAsync()
    {
        var workspacePath = TestWorkspaceFactory.CreateChangeImpactWorkspace();
        var graphStore = new CSharpGraphCacheStore();
        try
        {
            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var buildService = new CSharpGraphBuildService(
                new CSharpRoslynWorkspaceHost(NullLogger<CSharpRoslynWorkspaceHost>.Instance),
                graphStore,
                workspaceState);
            await buildService.BuildAsync(
                path: workspacePath,
                mode: "full",
                includeTests: true,
                includeGenerated: false,
                CancellationToken.None);

            var projectionService = new CSharpGraphProjectionService(buildService, graphStore, workspaceState);
            var result = await projectionService.ExportAsync(
                path: workspacePath,
                layout: "dot",
                focusSymbol: null,
                projectFilter: "Sample.App",
                includeTypes: true,
                includeMembers: false,
                includeDocuments: false,
                includeDi: true,
                includeEntrypoints: true,
                edgeKinds: null,
                maxNodes: 30,
                maxEdges: 60,
                rebuildIfMissing: false,
                writeToFile: true,
                outputPath: "artifacts/sample-app",
                CancellationToken.None);

            Assert.Equal("dot", result.Layout);
            Assert.Contains("digraph CSharpCodeGraph", result.Projection, StringComparison.Ordinal);
            Assert.Equal(Path.Combine("artifacts", "sample-app.dot"), result.ExportPath);
            Assert.True(File.Exists(Path.Combine(workspacePath, "artifacts", "sample-app.dot")));
            Assert.Contains(result.Nodes, node => node.Label.Contains("Project: Sample.App", StringComparison.Ordinal));
            Assert.Contains(result.Nodes, node => node.Label.Contains("Type: WeatherService", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Nodes, node => node.Label.Contains("WeatherServiceTests", StringComparison.Ordinal));
        }
        finally
        {
            var storagePath = graphStore.GetStoragePath(workspacePath);
            if (File.Exists(storagePath))
                File.Delete(storagePath);

            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
