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

            Assert.Contains("Solution root:", overview);
            Assert.Contains("Sample.slnx", overview);
            Assert.Contains("Sample.App [web]", overview);
            Assert.Contains("Sample.Core [classlib]", overview);
            Assert.Contains("Sample.Tests [test]", overview);
            Assert.Contains("Entrypoints: src/Sample.App/Program.cs", overview);
            Assert.Contains("Project refs: Sample.Core", overview);
            Assert.Contains("dotnet build Sample.slnx", overview);
            Assert.Contains("dotnet test Sample.slnx", overview);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
