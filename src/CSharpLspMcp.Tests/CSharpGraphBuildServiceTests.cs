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

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"graph-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
