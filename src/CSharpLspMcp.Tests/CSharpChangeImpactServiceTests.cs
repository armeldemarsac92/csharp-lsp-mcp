using CSharpLspMcp.Analysis.Architecture;
using CSharpLspMcp.Analysis.Graph;
using CSharpLspMcp.Analysis.Testing;
using CSharpLspMcp.Lsp;
using CSharpLspMcp.Storage.Graph;
using CSharpLspMcp.Workspace;
using CSharpLspMcp.Workspace.Roslyn;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CSharpLspMcp.Tests;

public class CSharpChangeImpactServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_AutoBuildsGraphAndReturnsCrossCuttingImpactAsync()
    {
        var workspacePath = TestWorkspaceFactory.CreateChangeImpactWorkspace();
        var graphStore = new CSharpGraphCacheStore();
        try
        {
            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);

            var lspClient = new LspClient(NullLogger<LspClient>.Instance, new SolutionFilter(NullLogger<SolutionFilter>.Instance));
            var workspaceSession = new CSharpWorkspaceSession(NullLogger<CSharpWorkspaceSession>.Instance, lspClient, workspaceState);
            var graphBuildService = new CSharpGraphBuildService(
                new CSharpRoslynWorkspaceHost(NullLogger<CSharpRoslynWorkspaceHost>.Instance),
                graphStore,
                workspaceState);
            var impactService = new CSharpChangeImpactService(
                graphBuildService,
                graphStore,
                new CSharpRegistrationAnalysisService(workspaceState),
                new CSharpEntrypointAnalysisService(workspaceState),
                new CSharpTestMapAnalysisService(workspaceSession, workspaceState),
                workspaceSession,
                workspaceState);

            var result = await impactService.AnalyzeAsync(
                symbolQuery: "Sample.App.WeatherService",
                filePath: null,
                includeTests: true,
                includeRegistrations: true,
                includeEntrypoints: true,
                rebuildIfMissing: true,
                maxResults: 10,
                CancellationToken.None);

            Assert.True(result.GraphBuiltDuringRequest);
            Assert.Contains(result.Targets, item => item.DisplayName == "WeatherService");
            Assert.Contains(result.IncomingCalls, item => item.DisplayName.Contains("Report", StringComparison.Ordinal));
            Assert.Contains(result.Registrations, item => item.ImplementationType == "WeatherService");
            Assert.Contains(result.Entrypoints, item => item.Category == "host_project" && item.Name == "Sample.App");
            Assert.Contains(result.RelatedTests, item => item.RelativePath.EndsWith("WeatherServiceTests.cs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.RecommendedFiles, item => item.RelativePath.EndsWith("DependencyInjection.cs", StringComparison.OrdinalIgnoreCase));
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
    public async Task AnalyzeAsync_ReturnsImplementationsForInterfaceTargetsAsync()
    {
        var workspacePath = TestWorkspaceFactory.CreateChangeImpactWorkspace();
        var graphStore = new CSharpGraphCacheStore();
        try
        {
            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);

            var lspClient = new LspClient(NullLogger<LspClient>.Instance, new SolutionFilter(NullLogger<SolutionFilter>.Instance));
            var workspaceSession = new CSharpWorkspaceSession(NullLogger<CSharpWorkspaceSession>.Instance, lspClient, workspaceState);
            var graphBuildService = new CSharpGraphBuildService(
                new CSharpRoslynWorkspaceHost(NullLogger<CSharpRoslynWorkspaceHost>.Instance),
                graphStore,
                workspaceState);
            await graphBuildService.BuildAsync(
                path: workspacePath,
                mode: "full",
                includeTests: true,
                includeGenerated: false,
                CancellationToken.None);

            var impactService = new CSharpChangeImpactService(
                graphBuildService,
                graphStore,
                new CSharpRegistrationAnalysisService(workspaceState),
                new CSharpEntrypointAnalysisService(workspaceState),
                new CSharpTestMapAnalysisService(workspaceSession, workspaceState),
                workspaceSession,
                workspaceState);

            var result = await impactService.AnalyzeAsync(
                symbolQuery: "Sample.Core.IWeatherService",
                filePath: null,
                includeTests: true,
                includeRegistrations: true,
                includeEntrypoints: false,
                rebuildIfMissing: false,
                maxResults: 10,
                CancellationToken.None);

            Assert.False(result.GraphBuiltDuringRequest);
            Assert.Contains(result.Targets, item => item.DisplayName == "IWeatherService");
            Assert.Contains(result.RelatedSymbols, item => item.Relation == "implements" && item.DisplayName == "WeatherService");
            Assert.Contains(result.IncomingCalls, item => item.DisplayName.Contains("Report", StringComparison.Ordinal));
            Assert.Contains(result.Registrations, item => item.ServiceType == "IWeatherService");
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
    public async Task AnalyzeAsync_DoesNotIncludeUnrelatedSameProjectEntrypointsForSimpleSymbolQueryAsync()
    {
        var workspacePath = TestWorkspaceFactory.CreateChangeImpactWorkspace();
        var graphStore = new CSharpGraphCacheStore();
        try
        {
            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);

            var lspClient = new LspClient(NullLogger<LspClient>.Instance, new SolutionFilter(NullLogger<SolutionFilter>.Instance));
            var workspaceSession = new CSharpWorkspaceSession(NullLogger<CSharpWorkspaceSession>.Instance, lspClient, workspaceState);
            var graphBuildService = new CSharpGraphBuildService(
                new CSharpRoslynWorkspaceHost(NullLogger<CSharpRoslynWorkspaceHost>.Instance),
                graphStore,
                workspaceState);
            await graphBuildService.BuildAsync(
                path: workspacePath,
                mode: "full",
                includeTests: true,
                includeGenerated: false,
                CancellationToken.None);

            var impactService = new CSharpChangeImpactService(
                graphBuildService,
                graphStore,
                new CSharpRegistrationAnalysisService(workspaceState),
                new CSharpEntrypointAnalysisService(workspaceState),
                new CSharpTestMapAnalysisService(workspaceSession, workspaceState),
                workspaceSession,
                workspaceState);

            var result = await impactService.AnalyzeAsync(
                symbolQuery: "Sample.App.GraphNodeProjectionService",
                filePath: null,
                includeTests: false,
                includeRegistrations: true,
                includeEntrypoints: true,
                rebuildIfMissing: false,
                maxResults: 10,
                CancellationToken.None);

            Assert.Contains(result.Targets, item => item.DisplayName == "GraphNodeProjectionService");
            Assert.DoesNotContain(result.Entrypoints, item => item.RelativePath.EndsWith("PublicCollectorEndpoints.cs", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.RecommendedFiles, item => item.RelativePath.EndsWith("PublicCollectorEndpoints.cs", StringComparison.OrdinalIgnoreCase));
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
    public async Task AnalyzeAsync_ReturnsRegistrationsAndPreciseEntrypointsForInterfaceServiceAsync()
    {
        var workspacePath = TestWorkspaceFactory.CreateChangeImpactWorkspace();
        var graphStore = new CSharpGraphCacheStore();
        try
        {
            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);

            var lspClient = new LspClient(NullLogger<LspClient>.Instance, new SolutionFilter(NullLogger<SolutionFilter>.Instance));
            var workspaceSession = new CSharpWorkspaceSession(NullLogger<CSharpWorkspaceSession>.Instance, lspClient, workspaceState);
            var graphBuildService = new CSharpGraphBuildService(
                new CSharpRoslynWorkspaceHost(NullLogger<CSharpRoslynWorkspaceHost>.Instance),
                graphStore,
                workspaceState);
            await graphBuildService.BuildAsync(
                path: workspacePath,
                mode: "full",
                includeTests: true,
                includeGenerated: false,
                CancellationToken.None);

            var impactService = new CSharpChangeImpactService(
                graphBuildService,
                graphStore,
                new CSharpRegistrationAnalysisService(workspaceState),
                new CSharpEntrypointAnalysisService(workspaceState),
                new CSharpTestMapAnalysisService(workspaceSession, workspaceState),
                workspaceSession,
                workspaceState);

            var result = await impactService.AnalyzeAsync(
                symbolQuery: "Sample.App.ICollectorReadService",
                filePath: null,
                includeTests: false,
                includeRegistrations: true,
                includeEntrypoints: true,
                rebuildIfMissing: false,
                maxResults: 10,
                CancellationToken.None);

            Assert.Contains(result.Targets, item => item.DisplayName == "ICollectorReadService");
            Assert.Contains(result.Registrations, item => item.ServiceType == "ICollectorReadService");
            Assert.Contains(result.Entrypoints, item => item.RelativePath.EndsWith("PublicCollectorEndpoints.cs", StringComparison.OrdinalIgnoreCase));
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
    public async Task AnalyzeAsync_ReturnsOwningTypeRegistrationForMemberQueryAsync()
    {
        var workspacePath = TestWorkspaceFactory.CreateChangeImpactWorkspace();
        var graphStore = new CSharpGraphCacheStore();
        try
        {
            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);

            var lspClient = new LspClient(NullLogger<LspClient>.Instance, new SolutionFilter(NullLogger<SolutionFilter>.Instance));
            var workspaceSession = new CSharpWorkspaceSession(NullLogger<CSharpWorkspaceSession>.Instance, lspClient, workspaceState);
            var graphBuildService = new CSharpGraphBuildService(
                new CSharpRoslynWorkspaceHost(NullLogger<CSharpRoslynWorkspaceHost>.Instance),
                graphStore,
                workspaceState);
            await graphBuildService.BuildAsync(
                path: workspacePath,
                mode: "full",
                includeTests: true,
                includeGenerated: false,
                CancellationToken.None);

            var impactService = new CSharpChangeImpactService(
                graphBuildService,
                graphStore,
                new CSharpRegistrationAnalysisService(workspaceState),
                new CSharpEntrypointAnalysisService(workspaceState),
                new CSharpTestMapAnalysisService(workspaceSession, workspaceState),
                workspaceSession,
                workspaceState);

            var result = await impactService.AnalyzeAsync(
                symbolQuery: "M:Sample.App.CollectorReadService.GetById",
                filePath: null,
                includeTests: false,
                includeRegistrations: true,
                includeEntrypoints: false,
                rebuildIfMissing: false,
                maxResults: 10,
                CancellationToken.None);

            Assert.Contains(result.Targets, item => item.DisplayName.Contains("CollectorReadService.GetById", StringComparison.Ordinal));
            Assert.Contains(result.Registrations, item => item.ImplementationType == "CollectorReadService");
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
