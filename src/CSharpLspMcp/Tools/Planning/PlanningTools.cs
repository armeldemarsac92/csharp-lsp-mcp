using System.ComponentModel;
using CSharpLspMcp.Analysis.Planning;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools.Planning;

[McpServerToolType]
public sealed class PlanningTools : CSharpToolBase
{
    private readonly CSharpChangePlanService _changePlanService;
    private readonly ILogger<PlanningTools> _logger;

    public PlanningTools(
        ILogger<PlanningTools> logger,
        CSharpChangePlanService changePlanService)
    {
        _logger = logger;
        _changePlanService = changePlanService;
    }

    [McpServerTool(Name = "csharp_plan_change")]
    [Description("Build an ordered change plan for a symbol or file: primary edit targets, affected projects, inspection order, and verification steps.")]
    public Task<string> PlanChangeAsync(
        [Description("Optional short description of the intended change.")] string? request = null,
        [Description("Fully qualified symbol name, documentation ID, or simple symbol query.")] string? symbolQuery = null,
        [Description("Optional file path to analyze instead of or in addition to a symbol query.")] string? filePath = null,
        [Description("Include related tests and test-driven verification in the plan (default: true).")] bool includeTests = true,
        [Description("Rebuild the graph automatically when it is missing or stale for impact planning (default: true).")] bool rebuildIfMissing = true,
        [Description("Maximum number of items to include per section (default: 10).")] int maxResults = 10,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_plan_change",
            format,
            ct => _changePlanService.PlanAsync(
                request,
                symbolQuery,
                filePath,
                includeTests,
                rebuildIfMissing,
                maxResults,
                ct),
            cancellationToken);
}
