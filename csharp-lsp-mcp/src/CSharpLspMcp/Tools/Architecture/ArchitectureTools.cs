using System.ComponentModel;
using CSharpLspMcp.Analysis.Architecture;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools.Architecture;

[McpServerToolType]
public sealed class ArchitectureTools : CSharpToolBase
{
    private readonly ILogger<ArchitectureTools> _logger;
    private readonly CSharpEntrypointAnalysisService _entrypointAnalysisService;
    private readonly CSharpProjectOverviewAnalysisService _projectOverviewAnalysisService;
    private readonly CSharpRegistrationAnalysisService _registrationAnalysisService;

    public ArchitectureTools(
        ILogger<ArchitectureTools> logger,
        CSharpProjectOverviewAnalysisService projectOverviewAnalysisService,
        CSharpEntrypointAnalysisService entrypointAnalysisService,
        CSharpRegistrationAnalysisService registrationAnalysisService)
    {
        _logger = logger;
        _projectOverviewAnalysisService = projectOverviewAnalysisService;
        _entrypointAnalysisService = entrypointAnalysisService;
        _registrationAnalysisService = registrationAnalysisService;
    }

    [McpServerTool(Name = "csharp_project_overview")]
    [Description("Summarize the current workspace: solution files, projects, frameworks, references, package dependencies, entrypoints, test projects, and suggested build commands.")]
    public Task<string> GetProjectOverviewAsync(
        [Description("Maximum number of projects to include in detail (default: 25)")] int maxProjects = 25,
        [Description("Maximum number of package references to show per project (default: 8)")] int maxPackagesPerProject = 8,
        [Description("Maximum number of project references to show per project (default: 8)")] int maxProjectReferencesPerProject = 8,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_project_overview",
            format,
            ct => _projectOverviewAnalysisService.GetProjectOverviewAsync(
                maxProjects,
                maxPackagesPerProject,
                maxProjectReferencesPerProject,
                ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_find_entrypoints")]
    [Description("Discover startup surfaces in the current workspace: host projects, Program.cs files, middleware pipeline calls, ASP.NET route registrations, and hosted/background services.")]
    public Task<string> FindEntrypointsAsync(
        [Description("Include direct ASP.NET route registrations such as MapGet/MapPost (default: true)")] bool includeAspNetRoutes = true,
        [Description("Include AddHostedService registrations and BackgroundService implementations (default: true)")] bool includeHostedServices = true,
        [Description("Include middleware pipeline calls from Program.cs such as UseAuthentication (default: true)")] bool includeMiddlewarePipeline = true,
        [Description("Maximum number of items to include per section (default: 20)")] int maxResults = 20,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_find_entrypoints",
            format,
            ct => _entrypointAnalysisService.FindEntrypointsAsync(
                includeAspNetRoutes,
                includeHostedServices,
                includeMiddlewarePipeline,
                maxResults,
                ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_find_registrations")]
    [Description("Trace DI registrations in the current workspace: service type, implementation, lifetime, registration site, and likely constructor consumers.")]
    public Task<string> FindRegistrationsAsync(
        [Description("Optional filter for service type, implementation type, or registration source text.")] string? query = null,
        [Description("Include likely constructor consumers of each registered service (default: true)")] bool includeConsumers = true,
        [Description("Maximum number of registrations and consumers to include per section (default: 20)")] int maxResults = 20,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_find_registrations",
            format,
            ct => _registrationAnalysisService.FindRegistrationsAsync(
                query,
                includeConsumers,
                maxResults,
                ct),
            cancellationToken);
}
