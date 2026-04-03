using System.ComponentModel;
using CSharpLspMcp.Analysis.Architecture;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools.Architecture;

[McpServerToolType]
public sealed class ArchitectureTools : CSharpToolBase
{
    private readonly ILogger<ArchitectureTools> _logger;
    private readonly CSharpProjectOverviewAnalysisService _projectOverviewAnalysisService;

    public ArchitectureTools(
        ILogger<ArchitectureTools> logger,
        CSharpProjectOverviewAnalysisService projectOverviewAnalysisService)
    {
        _logger = logger;
        _projectOverviewAnalysisService = projectOverviewAnalysisService;
    }

    [McpServerTool(Name = "csharp_project_overview")]
    [Description("Summarize the current workspace: solution files, projects, frameworks, references, package dependencies, entrypoints, test projects, and suggested build commands.")]
    public Task<string> GetProjectOverviewAsync(
        [Description("Maximum number of projects to include in detail (default: 25)")] int maxProjects = 25,
        [Description("Maximum number of package references to show per project (default: 8)")] int maxPackagesPerProject = 8,
        [Description("Maximum number of project references to show per project (default: 8)")] int maxProjectReferencesPerProject = 8,
        CancellationToken cancellationToken = default)
        => ExecuteToolAsync(
            _logger,
            "csharp_project_overview",
            ct => _projectOverviewAnalysisService.GetProjectOverviewAsync(
                maxProjects,
                maxPackagesPerProject,
                maxProjectReferencesPerProject,
                ct),
            cancellationToken);
}
