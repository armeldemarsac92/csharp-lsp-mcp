using CSharpLspMcp.Analysis.Architecture;
using CSharpLspMcp.Workspace;
using Xunit;

namespace CSharpLspMcp.Tests;

public sealed class CSharpProjectOverviewAnalysisServiceTests
{
    [Fact]
    public async Task GetProjectOverviewAsync_SummarizesProjectsAndCommands()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-overview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "Sample.slnx"), "<Solution />");
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "Sample.sln.DotSettings.user"), "ignored");

            var appProjectDirectory = Path.Combine(workspacePath, "src", "Sample.App");
            Directory.CreateDirectory(appProjectDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(appProjectDirectory, "Program.cs"),
                "var builder = WebApplication.CreateBuilder(args);");
            await File.WriteAllTextAsync(
                Path.Combine(appProjectDirectory, "Sample.App.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\..\src\Sample.Core\Sample.Core.csproj" />
                    <PackageReference Include="FluentValidation.DependencyInjectionExtensions" Version="11.9.0" />
                  </ItemGroup>
                </Project>
                """);

            var coreProjectDirectory = Path.Combine(workspacePath, "src", "Sample.Core");
            Directory.CreateDirectory(coreProjectDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(coreProjectDirectory, "Sample.Core.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var testProjectDirectory = Path.Combine(workspacePath, "tests", "Sample.Tests");
            Directory.CreateDirectory(testProjectDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(testProjectDirectory, "Sample.Tests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                    <ProjectReference Include="..\..\src\Sample.Core\Sample.Core.csproj" />
                  </ItemGroup>
                </Project>
                """);

            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var service = new CSharpProjectOverviewAnalysisService(workspaceState);

            var overview = await service.GetProjectOverviewAsync(10, 5, 5, CancellationToken.None);

            Assert.Equal(workspacePath, overview.SolutionRoot);
            Assert.Contains("Sample.slnx", overview.SolutionFiles);
            Assert.DoesNotContain("Sample.sln.DotSettings.user", overview.SolutionFiles);
            Assert.Equal(3, overview.TotalProjects);
            Assert.Contains(overview.Projects, project => project.Name == "Sample.App" && project.ProjectType == "web");
            Assert.Contains(overview.Projects, project => project.Name == "Sample.Core" && project.ProjectType == "classlib");
            Assert.Contains(overview.Projects, project => project.Name == "Sample.Tests" && project.ProjectType == "test");
            Assert.Contains(overview.Projects, project => project.Entrypoints.Contains("src/Sample.App/Program.cs"));
            Assert.Contains(overview.Projects, project => project.ProjectReferences.Contains("Sample.Core"));
            Assert.Contains("dotnet build Sample.slnx", overview.SuggestedCommands);
            Assert.Contains("dotnet test Sample.slnx", overview.SuggestedCommands);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
