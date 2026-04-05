using CSharpLspMcp.Analysis.Quality;
using CSharpLspMcp.Workspace;
using Xunit;

namespace CSharpLspMcp.Tests;

public sealed class CSharpDeadCodeAnalysisServiceTests
{
    [Fact]
    public async Task FindDeadCodeCandidatesAsync_FindsUnusedPrivateMembersAndInternalTypes()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-dead-code-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var sourceDirectory = Path.Combine(workspacePath, "src", "Sample.App");
            Directory.CreateDirectory(sourceDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory, "RuntimeService.cs"),
                """
                namespace Sample.App;

                internal sealed class InternalHelper
                {
                }

                internal sealed class UsedInternalHelper
                {
                }

                public sealed class RuntimeService
                {
                    private readonly string _unusedField;
                    private readonly UsedInternalHelper _usedHelper = new();

                    public string Execute()
                    {
                        return FormatValue();
                    }

                    private string FormatValue()
                    {
                        return _usedHelper.ToString() ?? string.Empty;
                    }

                    private void RemoveExpiredCache()
                    {
                    }
                }
                """);

            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var service = new CSharpDeadCodeAnalysisService(workspaceState);

            var summary = await service.FindDeadCodeCandidatesAsync(true, true, true, 20, CancellationToken.None);

            Assert.Contains(summary.Candidates, candidate => candidate.Kind == "private-field" && candidate.Name == "_unusedField");
            Assert.Contains(summary.Candidates, candidate => candidate.Kind == "private-method" && candidate.Name == "RemoveExpiredCache");
            Assert.Contains(summary.Candidates, candidate => candidate.Kind == "internal-type" && candidate.Name == "InternalHelper");
            Assert.DoesNotContain(summary.Candidates, candidate => candidate.Name == "FormatValue");
            Assert.DoesNotContain(summary.Candidates, candidate => candidate.Name == "UsedInternalHelper");
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task FindDeadCodeCandidatesAsync_CanLimitScopeToInternalTypes()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-dead-code-types-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var sourceDirectory = Path.Combine(workspacePath, "src", "Sample.App");
            Directory.CreateDirectory(sourceDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory, "InternalHelper.cs"),
                """
                namespace Sample.App;

                internal sealed class InternalHelper
                {
                }
                """);

            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var service = new CSharpDeadCodeAnalysisService(workspaceState);

            var summary = await service.FindDeadCodeCandidatesAsync(false, true, true, 20, CancellationToken.None);

            Assert.All(summary.Candidates, candidate => Assert.Equal("internal-type", candidate.Kind));
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task FindDeadCodeCandidatesAsync_DoesNotFlagInternalStaticExtensionContainers()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-dead-code-static-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var sourceDirectory = Path.Combine(workspacePath, "src", "Sample.Api");
            Directory.CreateDirectory(sourceDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory, "EndpointExtensions.cs"),
                """
                namespace Sample.Api;

                internal static class EndpointExtensions
                {
                    public static WebApplication MapRuntimeEndpoints(this WebApplication app)
                    {
                        return app;
                    }
                }

                public sealed class Startup
                {
                    public void Configure(WebApplication app)
                    {
                        app.MapRuntimeEndpoints();
                    }
                }
                """);

            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var service = new CSharpDeadCodeAnalysisService(workspaceState);

            var summary = await service.FindDeadCodeCandidatesAsync(false, true, true, 20, CancellationToken.None);

            Assert.DoesNotContain(summary.Candidates, candidate => candidate.Name == "EndpointExtensions");
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task FindDeadCodeCandidatesAsync_ExcludesTestsByDefault()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-dead-code-no-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var sourceDirectory = Path.Combine(workspacePath, "src", "Sample.App");
            var testsDirectory = Path.Combine(workspacePath, "tests", "Sample.App.Tests");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(testsDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory, "RuntimeService.cs"),
                """
                namespace Sample.App;

                public sealed class RuntimeService
                {
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(testsDirectory, "RuntimeServiceTests.cs"),
                """
                namespace Sample.App.Tests;

                public sealed class RuntimeServiceTests
                {
                    private void BuildFixture()
                    {
                    }
                }
                """);

            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var service = new CSharpDeadCodeAnalysisService(workspaceState);

            var summary = await service.FindDeadCodeCandidatesAsync(true, true, false, 20, CancellationToken.None);

            Assert.DoesNotContain(summary.Candidates, candidate => candidate.Name == "BuildFixture");
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
