using CSharpLspMcp.Analysis.Architecture;
using CSharpLspMcp.Workspace;
using Xunit;

namespace CSharpLspMcp.Tests;

public sealed class CSharpSemanticSearchAnalysisServiceTests
{
    [Fact]
    public async Task SearchAsync_FindsNamedSemanticMatchesAcrossModes()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-semantic-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var appDirectory = Path.Combine(workspacePath, "src", "Sample.Api");
            Directory.CreateDirectory(appDirectory);
            Directory.CreateDirectory(Path.Combine(appDirectory, "Hosted"));
            Directory.CreateDirectory(Path.Combine(workspacePath, "tests", "Sample.Api.Tests"));

            await File.WriteAllTextAsync(
                Path.Combine(appDirectory, "Endpoints.cs"),
                """
                public static class Endpoints
                {
                    public static void MapRoutes(IEndpointRouteBuilder endpoints)
                    {
                        endpoints.MapGet("/health", () => Results.Ok());
                        endpoints.MapPost("/login", () => Results.Ok());
                    }
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(appDirectory, "Hosted", "Worker.cs"),
                """
                using Microsoft.Extensions.DependencyInjection;
                using Microsoft.Extensions.Hosting;

                public sealed class Worker : BackgroundService
                {
                    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
                }

                public static class ServiceCollectionExtensions
                {
                    public static IServiceCollection AddSample(this IServiceCollection services)
                    {
                        services.AddHostedService<Worker>();
                        services.AddScoped<IFooService, FooService>();
                        return services;
                    }
                }

                public interface IFooService;
                public sealed class FooService : IFooService;
                """);

            await File.WriteAllTextAsync(
                Path.Combine(appDirectory, "Program.cs"),
                """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddOptions<BrokerOptions>()
                    .Bind(builder.Configuration.GetSection(BrokerOptions.SectionName));
                var app = builder.Build();
                app.UseAuthentication();
                app.UseAuthorization();
                app.Run();

                public sealed class BrokerOptions
                {
                    public const string SectionName = "Broker";
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(workspacePath, "tests", "Sample.Api.Tests", "TestEndpoints.cs"),
                """
                public static class TestEndpoints
                {
                    public static void MapRoutes(IEndpointRouteBuilder endpoints)
                    {
                        endpoints.MapGet("/test-only", () => Results.Ok());
                    }
                }
                """);

            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var service = new CSharpSemanticSearchAnalysisService(workspaceState);

            var endpointSummary = await service.SearchAsync("aspnet_endpoints", "Sample.Api", false, 10, CancellationToken.None);
            var hostedSummary = await service.SearchAsync("hosted_services", null, false, 10, CancellationToken.None);
            var registrationSummary = await service.SearchAsync("di_registrations", null, false, 10, CancellationToken.None);
            var bindingSummary = await service.SearchAsync("config_bindings", null, false, 10, CancellationToken.None);
            var middlewareSummary = await service.SearchAsync("middleware_pipeline", null, false, 10, CancellationToken.None);

            Assert.Contains("Matches (2):", endpointSummary);
            Assert.Contains("[MapGet]", endpointSummary);
            Assert.DoesNotContain("/test-only", endpointSummary);
            Assert.Contains("registration:Worker", hostedSummary);
            Assert.Contains("implementation:Worker", hostedSummary);
            Assert.Contains("Scoped:IFooService", registrationSummary);
            Assert.Contains("bind:BrokerOptions", bindingSummary);
            Assert.Contains("[UseAuthentication]", middlewareSummary);
            Assert.Contains("[UseAuthorization]", middlewareSummary);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_ReturnsHelpfulErrorForUnsupportedQuery()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-semantic-search-unsupported-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var service = new CSharpSemanticSearchAnalysisService(workspaceState);

            var summary = await service.SearchAsync("unknown_mode", null, false, 10, CancellationToken.None);

            Assert.Contains("Unsupported semantic search query", summary);
            Assert.Contains("aspnet_endpoints", summary);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
