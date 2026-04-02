using CSharpLspMcp.Lsp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CSharpLspMcp.Tests;

public sealed class SolutionFilterTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"solution-filter-tests-{Guid.NewGuid():N}");

    public SolutionFilterTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void FindSolutionFile_ReturnsSlnxWhenNoSlnExists()
    {
        var solutionPath = Path.Combine(_tempDirectory, "MeshBoard.slnx");
        File.WriteAllText(solutionPath, "<Solution />");

        var filter = CreateFilter();

        var resolved = filter.FindSolutionFile(_tempDirectory);

        Assert.Equal(solutionPath, resolved);
    }

    [Fact]
    public void ResolveWorkspaceLaunchContext_UsesSlnxWithoutFiltering()
    {
        var solutionPath = Path.Combine(_tempDirectory, "MeshBoard.slnx");
        File.WriteAllText(solutionPath, "<Solution />");

        var filter = CreateFilter();

        var context = filter.ResolveWorkspaceLaunchContext(_tempDirectory);

        Assert.Equal(_tempDirectory, context.WorkingDirectory);
        Assert.Equal(solutionPath, context.SolutionPath);
    }

    [Fact]
    public void ResolveWorkspaceLaunchContext_UsesFilteredSlnWhenUnsupportedProjectsExist()
    {
        var solutionPath = Path.Combine(_tempDirectory, "MeshBoard.sln");
        File.WriteAllText(
            solutionPath,
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAKE}") = "App", "App/App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAKE}") = "Installer", "Installer/Installer.wixproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Global
            	GlobalSection(ProjectConfigurationPlatforms) = postSolution
            		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            	EndGlobalSection
            EndGlobal
            """);

        var filter = CreateFilter();

        var context = filter.ResolveWorkspaceLaunchContext(_tempDirectory);

        Assert.NotEqual(_tempDirectory, context.WorkingDirectory);
        Assert.NotNull(context.SolutionPath);
        Assert.EndsWith(".sln", context.SolutionPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp", "MeshBoard"),
            context.WorkingDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private static SolutionFilter CreateFilter()
        => new(NullLogger<SolutionFilter>.Instance);
}
