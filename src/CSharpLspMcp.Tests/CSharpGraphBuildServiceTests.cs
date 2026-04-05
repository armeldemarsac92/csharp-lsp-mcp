using CSharpLspMcp.Analysis.Graph;
using CSharpLspMcp.Storage.Graph;
using CSharpLspMcp.Workspace;
using CSharpLspMcp.Workspace.Roslyn;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CSharpLspMcp.Tests;

public class CSharpGraphBuildServiceTests
{
    [Fact]
    public async Task BuildAsync_IndexesSingleProjectWorkspaceAsync()
    {
        var workspacePath = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspacePath, "App.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(workspacePath, "WeatherService.cs"),
                """
                namespace Sample.App;

                public interface IWeatherService
                {
                    string GetForecast();
                }

                public sealed class WeatherService : IWeatherService
                {
                    public string GetForecast() => "sunny";
                }
                """);

            var host = new CSharpRoslynWorkspaceHost(NullLogger<CSharpRoslynWorkspaceHost>.Instance);
            var store = new CSharpGraphCacheStore();
            var service = new CSharpGraphBuildService(
                host,
                store,
                new WorkspaceState());

            var result = await service.BuildAsync(
                path: workspacePath,
                mode: "full",
                includeTests: true,
                includeGenerated: false,
                CancellationToken.None);

            Assert.Equal(workspacePath, result.WorkspaceRoot);
            Assert.Equal(1, result.ProjectsIndexed);
            Assert.True(result.DocumentsIndexed >= 1);
            Assert.True(result.SymbolsIndexed >= 3);
            Assert.Contains(result.NodeCounts, item => item.Kind == WorkspaceGraphNodeKinds.Project && item.Count == 1);
            Assert.Contains(result.NodeCounts, item => item.Kind == WorkspaceGraphNodeKinds.Type);
            Assert.Contains(result.EdgeCounts, item => item.Kind == WorkspaceGraphEdgeKinds.Contains);

            var stats = await service.GetStatsAsync(workspacePath, CancellationToken.None);
            Assert.True(stats.GraphAvailable);
            Assert.Equal(result.SymbolsIndexed, stats.SymbolsIndexed);
        }
        finally
        {
            var store = new CSharpGraphCacheStore();
            var storagePath = store.GetStoragePath(workspacePath);
            if (File.Exists(storagePath))
                File.Delete(storagePath);

            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_IndexesCrossProjectRelationshipsAndCallEdgesAsync()
    {
        var workspacePath = CreateTempDirectory();
        try
        {
            var coreDirectory = Path.Combine(workspacePath, "Sample.Core");
            var appDirectory = Path.Combine(workspacePath, "Sample.App");
            Directory.CreateDirectory(coreDirectory);
            Directory.CreateDirectory(appDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(coreDirectory, "Sample.Core.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(coreDirectory, "IWeatherService.cs"),
                """
                namespace Sample.Core;

                public interface IWeatherService
                {
                    string GetForecast();
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(appDirectory, "Sample.App.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\Sample.Core\Sample.Core.csproj" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(appDirectory, "WeatherService.cs"),
                """
                using Sample.Core;

                namespace Sample.App;

                public sealed class WeatherService : IWeatherService
                {
                    public string GetForecast() => "sunny";
                }

                public sealed class WeatherReporter
                {
                    public string Report()
                    {
                        var service = new WeatherService();
                        return service.GetForecast();
                    }
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(workspacePath, "Sample.sln"),
                """
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                VisualStudioVersion = 17.0.31903.59
                MinimumVisualStudioVersion = 10.0.40219.1
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.Core", "Sample.Core\Sample.Core.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.App", "Sample.App\Sample.App.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                Global
                	GlobalSection(SolutionConfigurationPlatforms) = preSolution
                		Debug|Any CPU = Debug|Any CPU
                		Release|Any CPU = Release|Any CPU
                	EndGlobalSection
                	GlobalSection(ProjectConfigurationPlatforms) = postSolution
                		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|Any CPU
                		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|Any CPU
                		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.ActiveCfg = Release|Any CPU
                		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.Build.0 = Release|Any CPU
                	EndGlobalSection
                	GlobalSection(SolutionProperties) = preSolution
                		HideSolutionNode = FALSE
                	EndGlobalSection
                EndGlobal
                """);

            var host = new CSharpRoslynWorkspaceHost(NullLogger<CSharpRoslynWorkspaceHost>.Instance);
            var store = new CSharpGraphCacheStore();
            var service = new CSharpGraphBuildService(host, store, new WorkspaceState());

            await service.BuildAsync(
                path: workspacePath,
                mode: "full",
                includeTests: true,
                includeGenerated: false,
                CancellationToken.None);

            var snapshot = await store.LoadAsync(workspacePath, CancellationToken.None);

            Assert.NotNull(snapshot);
            Assert.Contains(snapshot.EdgeCounts, item => item.Kind == WorkspaceGraphEdgeKinds.Calls);
            Assert.Contains(
                snapshot.Edges,
                edge => edge.Kind == WorkspaceGraphEdgeKinds.Implements &&
                        edge.SourceId == "symbol:Sample.App::T:Sample.App.WeatherService" &&
                        edge.TargetId == "symbol:Sample.Core::T:Sample.Core.IWeatherService");
            Assert.Contains(
                snapshot.Edges,
                edge => edge.Kind == WorkspaceGraphEdgeKinds.Calls &&
                        edge.SourceId == "symbol:Sample.App::M:Sample.App.WeatherReporter.Report" &&
                        edge.TargetId == "symbol:Sample.App::M:Sample.App.WeatherService.GetForecast");
        }
        finally
        {
            var store = new CSharpGraphCacheStore();
            var storagePath = store.GetStoragePath(workspacePath);
            if (File.Exists(storagePath))
                File.Delete(storagePath);

            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"graph-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
