using CSharpLspMcp.Analysis.Architecture;
using CSharpLspMcp.Analysis.Graph;
using CSharpLspMcp.Analysis.Planning;
using CSharpLspMcp.Analysis.Testing;
using CSharpLspMcp.Lsp;
using CSharpLspMcp.Storage.Graph;
using CSharpLspMcp.Workspace;
using CSharpLspMcp.Workspace.Roslyn;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CSharpLspMcp.Tests;

public class CSharpChangePlanServiceTests
{
    [Fact]
    public async Task PlanAsync_BuildsOrderedPlanWithVerificationCommandsAsync()
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
            var changeImpactService = new CSharpChangeImpactService(
                graphBuildService,
                graphStore,
                new CSharpRegistrationAnalysisService(workspaceState),
                new CSharpEntrypointAnalysisService(workspaceState),
                new CSharpTestMapAnalysisService(workspaceSession, workspaceState),
                workspaceSession,
                workspaceState);
            var changePlanService = new CSharpChangePlanService(
                changeImpactService,
                new CSharpProjectOverviewAnalysisService(workspaceState),
                workspaceState);

            var result = await changePlanService.PlanAsync(
                request: "Change forecast behavior",
                symbolQuery: "Sample.App.WeatherService",
                filePath: null,
                includeTests: true,
                rebuildIfMissing: true,
                maxResults: 10,
                CancellationToken.None);

            Assert.True(result.GraphBuiltDuringRequest);
            Assert.NotEmpty(result.PrimaryTargets);
            Assert.All(result.PrimaryTargets, target => Assert.DoesNotContain("Tests", target.ProjectName, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.InspectionSteps, step => step.Title == "Inspect primary change targets");
            Assert.Contains(result.EditSteps, step => step.Title == "Reconcile runtime wiring if needed");
            Assert.Contains(result.VerificationSteps, step => step.Command == "dotnet build Sample.sln");
            Assert.Contains(result.VerificationSteps, step => step.Command == "dotnet test tests/Sample.App.Tests/Sample.App.Tests.csproj");
            Assert.Contains(result.SuggestedCommands, command => command == "dotnet build Sample.sln");
            Assert.Contains(result.SuggestedCommands, command => command == "dotnet test tests/Sample.App.Tests/Sample.App.Tests.csproj");
            Assert.Contains(result.ImpactedProjects, project => project.Name == "Sample.App");
            Assert.Contains(result.ImpactedProjects, project => project.Name == "Sample.App.Tests");
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
    public async Task PlanAsync_UsesExistingGraphWithoutRebuildAsync()
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

            var changeImpactService = new CSharpChangeImpactService(
                graphBuildService,
                graphStore,
                new CSharpRegistrationAnalysisService(workspaceState),
                new CSharpEntrypointAnalysisService(workspaceState),
                new CSharpTestMapAnalysisService(workspaceSession, workspaceState),
                workspaceSession,
                workspaceState);
            var changePlanService = new CSharpChangePlanService(
                changeImpactService,
                new CSharpProjectOverviewAnalysisService(workspaceState),
                workspaceState);

            var result = await changePlanService.PlanAsync(
                request: null,
                symbolQuery: "Sample.Core.IWeatherService",
                filePath: null,
                includeTests: true,
                rebuildIfMissing: false,
                maxResults: 10,
                CancellationToken.None);

            Assert.False(result.GraphBuiltDuringRequest);
            Assert.Contains(result.PrimaryTargets, target => target.DisplayName == "IWeatherService");
            Assert.Contains(result.EditSteps, step => step.Title == "Adjust dependent code paths");
            Assert.Contains(result.VerificationSteps, step => step.Kind == "build");
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
