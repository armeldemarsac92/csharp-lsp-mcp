using CSharpLspMcp.Analysis.Architecture;
using CSharpLspMcp.Workspace;
using Xunit;

namespace CSharpLspMcp.Tests;

public sealed class CSharpRegistrationAnalysisServiceTests
{
    [Fact]
    public async Task FindRegistrationsAsync_SummarizesRegistrationsAndConsumers()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-registrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var diDirectory = Path.Combine(workspacePath, "src", "Sample.App", "DependencyInjection");
            Directory.CreateDirectory(diDirectory);
            Directory.CreateDirectory(Path.Combine(workspacePath, "src", "Sample.App", "Consumers"));

            await File.WriteAllTextAsync(
                Path.Combine(diDirectory, "ServiceCollectionExtensions.cs"),
                """
                using Microsoft.Extensions.DependencyInjection;
                using Microsoft.Extensions.DependencyInjection.Extensions;

                namespace Sample.App.DependencyInjection;

                public interface IFooService;
                public sealed class FooService : IFooService;
                public interface IMessageSink;
                public sealed class KafkaMessageSink : IMessageSink;
                public interface IClock;
                public sealed class SystemClock : IClock;

                public static class ServiceCollectionExtensions
                {
                    public static IServiceCollection AddSample(this IServiceCollection services)
                    {
                        services.AddScoped<IFooService, FooService>();
                        services.AddSingleton<SystemClock>();
                        services.AddSingleton<IClock>(sp => sp.GetRequiredService<SystemClock>());
                        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessageSink, KafkaMessageSink>());
                        return services;
                    }
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(workspacePath, "src", "Sample.App", "Consumers", "FooConsumer.cs"),
                """
                namespace Sample.App.Consumers;

                public sealed class FooConsumer(IFooService fooService, IEnumerable<IMessageSink> sinks)
                {
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(workspacePath, "src", "Sample.App", "Consumers", "ClockConsumer.cs"),
                """
                namespace Sample.App.Consumers;

                public sealed class ClockConsumer
                {
                    public ClockConsumer(IClock clock)
                    {
                    }
                }
                """);

            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var service = new CSharpRegistrationAnalysisService(workspaceState);

            var summary = await service.FindRegistrationsAsync(null, true, 10, CancellationToken.None);

            Assert.Contains("Registrations (4):", summary);
            Assert.Contains("IFooService [Scoped]", summary);
            Assert.Contains("Implementation: FooService", summary);
            Assert.Contains("FooConsumer(IFooService, IEnumerable<IMessageSink>)", summary);
            Assert.Contains("IClock [Singleton]", summary);
            Assert.Contains("Kind: factory", summary);
            Assert.Contains("ClockConsumer(IClock)", summary);
            Assert.Contains("IMessageSink [Singleton]", summary);
            Assert.Contains("Kind: enumerable", summary);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task FindRegistrationsAsync_FiltersByQuery()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-registrations-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var diDirectory = Path.Combine(workspacePath, "src", "Sample.App");
            Directory.CreateDirectory(diDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(diDirectory, "ServiceCollectionExtensions.cs"),
                """
                using Microsoft.Extensions.DependencyInjection;

                namespace Sample.App;

                public interface IFooService;
                public sealed class FooService : IFooService;
                public interface IBarService;
                public sealed class BarService : IBarService;

                public static class ServiceCollectionExtensions
                {
                    public static IServiceCollection AddSample(this IServiceCollection services)
                    {
                        services.AddScoped<IFooService, FooService>();
                        services.AddScoped<IBarService, BarService>();
                        return services;
                    }
                }
                """);

            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var service = new CSharpRegistrationAnalysisService(workspaceState);

            var summary = await service.FindRegistrationsAsync("Foo", false, 10, CancellationToken.None);

            Assert.Contains("IFooService [Scoped]", summary);
            Assert.DoesNotContain("IBarService [Scoped]", summary);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task FindRegistrationsAsync_DoesNotTreatLoggerOfImplementationAsConsumer()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-registrations-logger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var sourceDirectory = Path.Combine(workspacePath, "src", "Sample.App");
            Directory.CreateDirectory(sourceDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory, "Services.cs"),
                """
                using Microsoft.Extensions.DependencyInjection;
                using Microsoft.Extensions.Logging;

                namespace Sample.App;

                public interface IFooService;
                public sealed class FooService : IFooService;

                public static class ServiceCollectionExtensions
                {
                    public static IServiceCollection AddSample(this IServiceCollection services)
                    {
                        services.AddScoped<IFooService, FooService>();
                        return services;
                    }
                }

                public sealed class FooWorker
                {
                    public FooWorker(ILogger<FooService> logger)
                    {
                    }
                }
                """);

            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var service = new CSharpRegistrationAnalysisService(workspaceState);

            var summary = await service.FindRegistrationsAsync("IFooService", true, 10, CancellationToken.None);

            Assert.Contains("Consumers (0):", summary);
            Assert.DoesNotContain("FooWorker(ILogger<FooService>)", summary);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
