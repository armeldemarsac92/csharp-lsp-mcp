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

            Assert.Equal(4, summary.TotalRegistrations);

            var fooRegistration = Assert.Single(summary.Registrations.Where(item => item.ServiceType == "IFooService"));
            Assert.Equal("FooService", fooRegistration.ImplementationType);
            Assert.Contains(fooRegistration.Consumers, consumer => consumer.DisplayText.Contains("FooConsumer(IFooService, IEnumerable<IMessageSink>)", StringComparison.Ordinal));

            var clockRegistration = Assert.Single(summary.Registrations.Where(item => item.ServiceType == "IClock"));
            Assert.True(clockRegistration.IsFactory);
            Assert.Contains(clockRegistration.Consumers, consumer => consumer.DisplayText.Contains("ClockConsumer(IClock)", StringComparison.Ordinal));

            var sinkRegistration = Assert.Single(summary.Registrations.Where(item => item.ServiceType == "IMessageSink"));
            Assert.True(sinkRegistration.IsEnumerable);
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

            Assert.Single(summary.Registrations);
            Assert.Equal("IFooService", summary.Registrations[0].ServiceType);
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

            var registration = Assert.Single(summary.Registrations);
            Assert.Empty(registration.Consumers);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task FindRegistrationsAsync_DoesNotCrossWireDuplicateServiceNamesAcrossProjects()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-registrations-duplicates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var workerADirectory = Path.Combine(workspacePath, "src", "Worker.A");
            var workerBDirectory = Path.Combine(workspacePath, "src", "Worker.B");
            Directory.CreateDirectory(workerADirectory);
            Directory.CreateDirectory(workerBDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(workerADirectory, "Worker.A.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(workerBDirectory, "Worker.B.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            await File.WriteAllTextAsync(
                Path.Combine(workerADirectory, "Services.cs"),
                """
                using Microsoft.Extensions.DependencyInjection;

                namespace Worker.A;

                public interface IDbRepository;
                public sealed class DbRepository : IDbRepository;

                public static class WorkerACollectionExtensions
                {
                    public static IServiceCollection AddWorkerA(this IServiceCollection services)
                    {
                        services.AddScoped<IDbRepository, DbRepository>();
                        return services;
                    }
                }

                public sealed class WorkerAConsumer
                {
                    public WorkerAConsumer(IDbRepository repository)
                    {
                    }
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(workerBDirectory, "Services.cs"),
                """
                using Microsoft.Extensions.DependencyInjection;

                namespace Worker.B;

                public interface IDbRepository;
                public sealed class DbRepository : IDbRepository;

                public static class WorkerBCollectionExtensions
                {
                    public static IServiceCollection AddWorkerB(this IServiceCollection services)
                    {
                        services.AddSingleton<IDbRepository, DbRepository>();
                        return services;
                    }
                }

                public sealed class WorkerBConsumer
                {
                    public WorkerBConsumer(IDbRepository repository)
                    {
                    }
                }
                """);

            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var service = new CSharpRegistrationAnalysisService(workspaceState);

            var summary = await service.FindRegistrationsAsync("IDbRepository", true, 10, CancellationToken.None);

            Assert.Equal(2, summary.Registrations.Length);
            var workerARegistration = Assert.Single(summary.Registrations.Where(item => item.Project == "Worker.A"));
            var workerBRegistration = Assert.Single(summary.Registrations.Where(item => item.Project == "Worker.B"));

            Assert.All(workerARegistration.Consumers, consumer => Assert.Equal("Worker.A", consumer.Project));
            Assert.All(workerBRegistration.Consumers, consumer => Assert.Equal("Worker.B", consumer.Project));
            Assert.Contains(workerARegistration.Consumers, consumer => consumer.DisplayText.Contains("WorkerAConsumer(IDbRepository)", StringComparison.Ordinal));
            Assert.Contains(workerBRegistration.Consumers, consumer => consumer.DisplayText.Contains("WorkerBConsumer(IDbRepository)", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task FindRegistrationsAsync_TracksHttpMessageHandlerConsumers()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "csharp-lsp-mcp-registrations-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var sourceDirectory = Path.Combine(workspacePath, "src", "Sample.Api");
            Directory.CreateDirectory(sourceDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory, "Sample.Api.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory, "HttpClients.cs"),
                """
                using System.Net.Http;
                using Microsoft.Extensions.DependencyInjection;

                namespace Sample.Api;

                public interface IFooClient;
                public sealed class FooClient : IFooClient;
                public sealed class AuthHeaderHandler : DelegatingHandler;

                public static class ServiceCollectionExtensions
                {
                    public static IServiceCollection AddSample(this IServiceCollection services)
                    {
                        services.AddTransient<AuthHeaderHandler>();
                        services.AddHttpClient<IFooClient, FooClient>()
                            .AddHttpMessageHandler<AuthHeaderHandler>();
                        return services;
                    }
                }
                """);

            var workspaceState = new WorkspaceState();
            workspaceState.SetPath(workspacePath);
            var service = new CSharpRegistrationAnalysisService(workspaceState);

            var summary = await service.FindRegistrationsAsync("AuthHeaderHandler", true, 10, CancellationToken.None);

            var registration = Assert.Single(summary.Registrations);
            Assert.Contains(registration.Consumers, consumer => consumer.DisplayText.Contains("AddHttpMessageHandler<AuthHeaderHandler>()", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
