using CSharpLspMcp.Analysis.Architecture;
using CSharpLspMcp.Workspace;
using Xunit;

namespace CSharpLspMcp.Tests;

public sealed class CSharpEntrypointAnalysisServiceTests
{
    [Fact]
    public async Task FindEntrypointsAsync_SummarizesHostsRoutesAndHostedServices()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-entrypoints-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var apiProjectDirectory = Path.Combine(workspacePath, "src", "Sample.Api");
            Directory.CreateDirectory(Path.Combine(apiProjectDirectory, "Extensions"));
            Directory.CreateDirectory(Path.Combine(apiProjectDirectory, "Hosted"));

            await File.WriteAllTextAsync(
                Path.Combine(apiProjectDirectory, "Program.cs"),
                """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddHostedService<RuntimeWorker>();
                var app = builder.Build();
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapApiEndpoints();
                app.Run();
                """);

            await File.WriteAllTextAsync(
                Path.Combine(apiProjectDirectory, "Extensions", "EndpointExtensions.cs"),
                """
                public static class EndpointExtensions
                {
                    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
                    {
                        app.MapGet("/health", () => Results.Ok());
                        app.MapPost("/login", () => Results.Ok());
                        return app;
                    }
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(apiProjectDirectory, "Hosted", "RuntimeWorker.cs"),
                """
                using Microsoft.Extensions.Hosting;

                internal sealed class RuntimeWorker : BackgroundService
                {
                    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(apiProjectDirectory, "Sample.Api.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var workerProjectDirectory = Path.Combine(workspacePath, "src", "Sample.Worker");
            Directory.CreateDirectory(workerProjectDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(workerProjectDirectory, "Program.cs"),
                """
                var builder = Host.CreateApplicationBuilder(args);
                var host = builder.Build();
                host.Run();
                """);

            await File.WriteAllTextAsync(
                Path.Combine(workerProjectDirectory, "Sample.Worker.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """);

            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var service = new CSharpEntrypointAnalysisService(workspaceState);

            var summary = await service.FindEntrypointsAsync(true, true, true, 20, CancellationToken.None);

            Assert.Equal(workspacePath, summary.SolutionRoot);
            Assert.Equal(2, summary.HostProjects.Length);
            Assert.Contains(summary.HostProjects, project => project.Name == "Sample.Api" && project.ProjectType == "web");
            Assert.Contains(summary.HostProjects, project => project.ProgramPath == "src/Sample.Api/Program.cs");
            Assert.Contains(summary.HostProjects, project => project.MiddlewareCalls.Contains("UseAuthentication"));
            Assert.Contains(summary.HostProjects, project => project.EndpointCompositionCalls.Contains("app.MapApiEndpoints()"));
            Assert.Contains(summary.AspNetRoutes, route => route.Text.Contains("MapGet(\"/health\"", StringComparison.Ordinal));
            Assert.Contains(summary.HostedServiceRegistrations, route => route.Text.Contains("AddHostedService<RuntimeWorker>()", StringComparison.Ordinal));
            Assert.Contains(summary.BackgroundServiceImplementations, route => route.Text.Contains("RuntimeWorker : BackgroundService", StringComparison.Ordinal));
            Assert.Contains(summary.HostProjects, project => project.Name == "Sample.Worker" && project.ProjectType == "worker");
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
