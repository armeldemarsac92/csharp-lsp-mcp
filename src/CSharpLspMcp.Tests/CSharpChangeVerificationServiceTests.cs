using CSharpLspMcp.Analysis.Architecture;
using CSharpLspMcp.Analysis.Graph;
using CSharpLspMcp.Analysis.Lsp;
using CSharpLspMcp.Analysis.Planning;
using CSharpLspMcp.Analysis.Testing;
using CSharpLspMcp.Analysis.Verification;
using CSharpLspMcp.Lsp;
using CSharpLspMcp.Storage.Graph;
using CSharpLspMcp.Workspace;
using CSharpLspMcp.Workspace.Roslyn;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CSharpLspMcp.Tests;

public class CSharpChangeVerificationServiceTests
{
    [Fact]
    public async Task VerifyAsync_UsesPlanAndFocusedDiagnosticsAsync()
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
            var planService = new CSharpChangePlanService(
                impactService,
                new CSharpProjectOverviewAnalysisService(workspaceState),
                workspaceState);
            var diagnosticsProvider = new FakeWorkspaceDiagnosticsProvider(workspacePath);
            var verificationService = new CSharpChangeVerificationService(
                diagnosticsProvider,
                planService,
                new CSharpProjectOverviewAnalysisService(workspaceState),
                workspaceState);

            var result = await verificationService.VerifyAsync(
                request: "Change forecast behavior",
                symbolQuery: "Sample.App.WeatherService",
                filePath: null,
                changedFiles: ["src/Sample.App/WeatherService.cs"],
                includeTests: true,
                rebuildIfMissing: true,
                minimumSeverity: "WARNING",
                excludeDiagnosticCodes: null,
                excludeDiagnosticSources: null,
                maxResults: 10,
                CancellationToken.None);

            Assert.True(result.GraphBuiltDuringRequest);
            Assert.Contains("dotnet build Sample.sln", result.BuildCommands);
            Assert.Contains("dotnet test tests/Sample.App.Tests/Sample.App.Tests.csproj", result.TestCommands);
            Assert.Contains(result.ImpactedProjects, project => project.Name == "Sample.App");
            Assert.Contains(result.ImpactedProjects, project => project.Name == "Sample.App.Tests");
            Assert.Contains(result.DiagnosticFocusFiles, file => file == "src/Sample.App/WeatherService.cs");
            Assert.All(result.Diagnostics, document =>
                Assert.DoesNotContain("Unrelated", document.RelativePath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.VerificationSteps, step => step.Kind == "build");
            Assert.Contains(result.VerificationSteps, step => step.Kind == "test");
            Assert.Contains(result.VerificationSteps, step => step.Kind == "diagnostics");
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
    public async Task VerifyAsync_UsesChangedFilesWithoutPlanAsync()
    {
        var workspacePath = TestWorkspaceFactory.CreateChangeImpactWorkspace();
        try
        {
            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var diagnosticsProvider = new FakeWorkspaceDiagnosticsProvider(workspacePath);
            var verificationService = new CSharpChangeVerificationService(
                diagnosticsProvider,
                changePlanService: null!,
                new CSharpProjectOverviewAnalysisService(workspaceState),
                workspaceState);

            var result = await verificationService.VerifyAsync(
                request: null,
                symbolQuery: null,
                filePath: null,
                changedFiles: ["tests/Sample.App.Tests/WeatherServiceTests.cs"],
                includeTests: true,
                rebuildIfMissing: false,
                minimumSeverity: "WARNING",
                excludeDiagnosticCodes: null,
                excludeDiagnosticSources: null,
                maxResults: 10,
                CancellationToken.None);

            Assert.False(result.GraphBuiltDuringRequest);
            Assert.Contains(result.BuildCommands, command => command == "dotnet build Sample.sln");
            Assert.Contains(result.TestCommands, command => command == "dotnet test tests/Sample.App.Tests/Sample.App.Tests.csproj");
            Assert.Contains(result.ImpactedProjects, project => project.Name == "Sample.App.Tests");
            Assert.Single(result.Diagnostics);
            Assert.Equal("tests/Sample.App.Tests/WeatherServiceTests.cs", result.Diagnostics[0].RelativePath);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private sealed class FakeWorkspaceDiagnosticsProvider : IWorkspaceDiagnosticsProvider
    {
        private readonly string _workspacePath;

        public FakeWorkspaceDiagnosticsProvider(string workspacePath)
        {
            _workspacePath = workspacePath;
        }

        public Task<CSharpWorkspaceAnalysisService.WorkspaceDiagnosticsResponse> GetWorkspaceDiagnosticsAsync(
            int maxDocuments,
            int maxDiagnosticsPerDocument,
            string minimumSeverity,
            bool includeGenerated,
            bool includeTests,
            IReadOnlyCollection<string>? excludePaths,
            IReadOnlyCollection<string>? excludeDiagnosticCodes,
            IReadOnlyCollection<string>? excludeDiagnosticSources,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new CSharpWorkspaceAnalysisService.WorkspaceDiagnosticsResponse(
                    Summary: "Fake diagnostics",
                    SolutionRoot: _workspacePath,
                    MinimumSeverity: minimumSeverity,
                    IncludeGenerated: includeGenerated,
                    IncludeTests: includeTests,
                    ExcludedPathPatterns: excludePaths?.ToArray() ?? Array.Empty<string>(),
                    ExcludedDiagnosticCodes: excludeDiagnosticCodes?.ToArray() ?? Array.Empty<string>(),
                    ExcludedDiagnosticSources: excludeDiagnosticSources?.ToArray() ?? Array.Empty<string>(),
                    TotalDiagnostics: 3,
                    TotalDocuments: 3,
                    Documents:
                    [
                        new CSharpWorkspaceAnalysisService.WorkspaceDiagnosticDocument(
                            Path.Combine(_workspacePath, "src", "Sample.App", "WeatherService.cs"),
                            1,
                            [
                                new CSharpWorkspaceAnalysisService.WorkspaceDiagnosticItem(
                                    "WARNING",
                                    "CS8602",
                                    "csharp",
                                    12,
                                    5,
                                    "Possible null reference.")
                            ],
                            0),
                        new CSharpWorkspaceAnalysisService.WorkspaceDiagnosticDocument(
                            Path.Combine(_workspacePath, "tests", "Sample.App.Tests", "WeatherServiceTests.cs"),
                            1,
                            [
                                new CSharpWorkspaceAnalysisService.WorkspaceDiagnosticItem(
                                    "WARNING",
                                    "CS0219",
                                    "csharp",
                                    7,
                                    9,
                                    "Variable assigned but never used.")
                            ],
                            0),
                        new CSharpWorkspaceAnalysisService.WorkspaceDiagnosticDocument(
                            Path.Combine(_workspacePath, "src", "Sample.App", "Unrelated.cs"),
                            1,
                            [
                                new CSharpWorkspaceAnalysisService.WorkspaceDiagnosticItem(
                                    "WARNING",
                                    "CS0168",
                                    "csharp",
                                    3,
                                    9,
                                    "Unused variable.")
                            ],
                            0)
                    ],
                    TruncatedDocuments: 0));
        }
    }
}
