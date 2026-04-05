using System.Reflection;
using CSharpLspMcp.Workspace.Roslyn;
using Xunit;

namespace CSharpLspMcp.Tests;

public class CSharpRoslynWorkspaceHostTests
{
    [Fact]
    public void ResolveWorkspaceTargetPath_PrefersSolutionFilesOverProjects()
    {
        var workspacePath = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(workspacePath, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            File.WriteAllText(Path.Combine(workspacePath, "App.sln"), string.Empty);
            File.WriteAllText(Path.Combine(workspacePath, "App.slnx"), string.Empty);

            var method = typeof(CSharpRoslynWorkspaceHost).GetMethod(
                "ResolveWorkspaceTargetPath",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var resolved = (string)method.Invoke(null, [workspacePath])!;

            Assert.EndsWith("App.slnx", resolved, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public void ParseSlnxProjectPaths_ReturnsNestedProjectPaths()
    {
        var workspacePath = CreateTempDirectory();
        try
        {
            var slnxPath = Path.Combine(workspacePath, "Sample.slnx");
            File.WriteAllText(
                slnxPath,
                """
                <Solution>
                  <Folder Name="/Api/">
                    <Project Path="src/Sample.Api/Sample.Api.csproj" />
                  </Folder>
                  <Folder Name="/Tests/">
                    <Project Path="tests/Sample.Api.Tests/Sample.Api.Tests.csproj" />
                  </Folder>
                </Solution>
                """);

            var method = typeof(CSharpRoslynWorkspaceHost).GetMethod(
                "ParseSlnxProjectPaths",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var projectPaths = (string[])method.Invoke(null, [slnxPath])!;

            Assert.Collection(
                projectPaths,
                path => Assert.Equal("src/Sample.Api/Sample.Api.csproj", path),
                path => Assert.Equal("tests/Sample.Api.Tests/Sample.Api.Tests.csproj", path));
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"roslyn-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
