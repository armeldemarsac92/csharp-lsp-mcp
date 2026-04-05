using System.ComponentModel;
using CSharpLspMcp.Analysis.Graph;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools.Graph;

[McpServerToolType]
public sealed class GraphTools : CSharpToolBase
{
    private readonly CSharpGraphBuildService _graphBuildService;
    private readonly ILogger<GraphTools> _logger;

    public GraphTools(
        ILogger<GraphTools> logger,
        CSharpGraphBuildService graphBuildService)
    {
        _logger = logger;
        _graphBuildService = graphBuildService;
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
}
