using System.ComponentModel;
using CSharpLspMcp.Analysis.Graph;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools.Graph;

[McpServerToolType]
public sealed class GraphTools : CSharpToolBase
{
    private readonly CSharpGraphBuildService _graphBuildService;
    private readonly CSharpChangeImpactService _changeImpactService;
    private readonly ILogger<GraphTools> _logger;

    public GraphTools(
        ILogger<GraphTools> logger,
        CSharpGraphBuildService graphBuildService,
        CSharpChangeImpactService changeImpactService)
    {
        _logger = logger;
        _graphBuildService = graphBuildService;
        _changeImpactService = changeImpactService;
    }

    [McpServerTool(Name = "csharp_build_code_graph")]
    [Description("Build or refresh a persistent Roslyn-backed code graph for the current workspace or an explicit solution/project path.")]
    public Task<string> BuildCodeGraphAsync(
        [Description("Optional workspace, solution, or project path. Uses the current workspace when omitted.")] string? path = null,
        [Description("Build mode: incremental (default) or full. Incremental currently performs a full rebuild with a warning.")] string mode = "incremental",
        [Description("Include test projects and test paths in the graph (default: true).")] bool includeTests = true,
        [Description("Include generated files such as obj, bin, and *.g.cs outputs (default: false).")] bool includeGenerated = false,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_build_code_graph",
            format,
            ct => _graphBuildService.BuildAsync(path, mode, includeTests, includeGenerated, ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_graph_stats")]
    [Description("Read the persisted code graph summary for the current workspace or an explicit solution/project path.")]
    public Task<string> GetGraphStatsAsync(
        [Description("Optional workspace, solution, or project path. Uses the current workspace when omitted.")] string? path = null,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_graph_stats",
            format,
            ct => _graphBuildService.GetStatsAsync(path, ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_change_impact")]
    [Description("Estimate downstream impact for changing a symbol or file using the persisted code graph plus architecture and test heuristics.")]
    public Task<string> GetChangeImpactAsync(
        [Description("Fully qualified symbol name, documentation ID, or simple symbol query.")] string? symbolQuery = null,
        [Description("Optional file path to analyze instead of or in addition to a symbol query.")] string? filePath = null,
        [Description("Include related tests in the impact report (default: true).")] bool includeTests = true,
        [Description("Include matching DI registrations and consumers in the impact report (default: true).")] bool includeRegistrations = true,
        [Description("Include matching entrypoints, routes, hosted services, and handlers (default: true).")] bool includeEntrypoints = true,
        [Description("Rebuild the graph automatically when it is missing or too old for call-impact analysis (default: true).")] bool rebuildIfMissing = true,
        [Description("Maximum number of items to return per section (default: 20).")] int maxResults = 20,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_change_impact",
            format,
            ct => _changeImpactService.AnalyzeAsync(
                symbolQuery,
                filePath,
                includeTests,
                includeRegistrations,
                includeEntrypoints,
                rebuildIfMissing,
                maxResults,
                ct),
            cancellationToken);
}
