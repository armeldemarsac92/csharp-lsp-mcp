using CSharpLspMcp.Analysis.Testing;
using CSharpLspMcp.Lsp;
using CSharpLspMcp.Workspace;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CSharpLspMcp.Tests;

public sealed class CSharpTestMapAnalysisServiceTests
{
    [Fact]
    public async Task GetTestMapAsync_FindsTestsForProductionFile()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-test-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var sourceDirectory = Path.Combine(workspacePath, "src", "Sample.App");
            var testsDirectory = Path.Combine(workspacePath, "tests", "Sample.App.Tests");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(testsDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory, "TopicDiscoveryService.cs"),
                """
                namespace Sample.App;

                public sealed class TopicDiscoveryService
                {
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(testsDirectory, "TopicDiscoveryServiceTests.cs"),
                """
                public sealed class TopicDiscoveryServiceTests
                {
                    public void BuildsTopicSnapshot()
                    {
                        var service = new TopicDiscoveryService();
                    }
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(testsDirectory, "MeshtasticIngestionServiceTests.cs"),
                """
                public sealed class MeshtasticIngestionServiceTests
                {
                    public void UsesTopicDiscoveryService()
                    {
                        var dependency = new TopicDiscoveryService();
                    }
                }
                """);

            var service = CreateService(workspacePath);

            var summary = await service.GetTestMapAsync("src/Sample.App/TopicDiscoveryService.cs", null, 10, CancellationToken.None);

            Assert.Equal("src/Sample.App/TopicDiscoveryService.cs", summary.Target);
            Assert.Equal(2, summary.TotalMatches);
            Assert.Contains(summary.Matches, match => match.RelativePath.EndsWith("TopicDiscoveryServiceTests.cs", StringComparison.Ordinal));
            Assert.Contains(summary.Matches, match => match.RelativePath.EndsWith("MeshtasticIngestionServiceTests.cs", StringComparison.Ordinal));
            Assert.Contains(summary.Matches.SelectMany(match => match.Reasons), reason => reason == "file:TopicDiscoveryService");
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task GetTestMapAsync_FindsTestsForSymbolQuery()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-test-map-symbol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var testsDirectory = Path.Combine(workspacePath, "tests", "Sample.App.Tests");
            Directory.CreateDirectory(testsDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(testsDirectory, "AppUserPrincipalFactoryTests.cs"),
                """
                public sealed class AppUserPrincipalFactoryTests
                {
                    public void CreatePrincipal_ShouldPopulateExpectedClaims()
                    {
                        var principal = AppUserPrincipalFactory.CreatePrincipal(default!, "Cookies");
                    }
                }
                """);

            var service = CreateService(workspacePath);

            var summary = await service.GetTestMapAsync(null, "Sample.Auth.AppUserPrincipalFactory", 10, CancellationToken.None);

            Assert.Equal("Sample.Auth.AppUserPrincipalFactory", summary.Target);
            Assert.Contains(summary.Matches, match => match.RelativePath.EndsWith("AppUserPrincipalFactoryTests.cs", StringComparison.Ordinal));
            Assert.Contains(summary.Matches.SelectMany(match => match.Reasons), reason => reason.StartsWith("content:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task GetTestMapAsync_RequiresInput()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-test-map-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var service = CreateService(workspacePath);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetTestMapAsync(null, null, 10, CancellationToken.None));

            Assert.Contains("Provide either filePath or symbolQuery", ex.Message);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private static CSharpTestMapAnalysisService CreateService(string workspacePath)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var solutionFilter = new SolutionFilter(loggerFactory.CreateLogger<SolutionFilter>());
        var lspClient = new LspClient(loggerFactory.CreateLogger<LspClient>(), solutionFilter);
        var workspaceState = new WorkspaceState();
        workspaceState.SetPath(workspacePath);
        var workspaceSession = new CSharpWorkspaceSession(
            loggerFactory.CreateLogger<CSharpWorkspaceSession>(),
            lspClient,
            workspaceState);

        return new CSharpTestMapAnalysisService(workspaceSession, workspaceState);
    }
}
