using System.ComponentModel;
using CSharpLspMcp.Analysis.Architecture;
using CSharpLspMcp.Analysis.Lsp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools.Search;

[McpServerToolType]
public sealed class SearchTools : CSharpToolBase
{
    private readonly ILogger<SearchTools> _logger;
    private readonly CSharpSemanticSearchAnalysisService _semanticSearchAnalysisService;
    private readonly CSharpSearchAnalysisService _searchAnalysisService;

    public SearchTools(
        ILogger<SearchTools> logger,
        CSharpSearchAnalysisService searchAnalysisService,
        CSharpSemanticSearchAnalysisService semanticSearchAnalysisService)
    {
        _logger = logger;
        _searchAnalysisService = searchAnalysisService;
        _semanticSearchAnalysisService = semanticSearchAnalysisService;
    }

    [McpServerTool(Name = "csharp_search_symbols")]
    [Description("Search symbols across the current workspace by name. Results include symbol kind, container, and source location.")]
    public Task<string> SearchSymbolsAsync(
        [Description("Search query for symbol name. Can be empty to inspect top-ranked workspace symbols.")] string query,
        [Description("Maximum number of results to return (default: 20)")] int maxResults = 20,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_search_symbols",
            format,
            ct => _searchAnalysisService.SearchSymbolsAsync(query, maxResults, ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_semantic_search")]
    [Description("Run named semantic searches over the current workspace such as aspnet_endpoints, hosted_services, di_registrations, config_bindings, or middleware_pipeline.")]
    public Task<string> SemanticSearchAsync(
        [Description("Named search mode: aspnet_endpoints, hosted_services, di_registrations, config_bindings, or middleware_pipeline.")] string query,
        [Description("Optional project or path fragment filter.")] string? projectFilter = null,
        [Description("Include matches from test projects and test paths (default: false).")] bool includeTests = false,
        [Description("Maximum number of results to return (default: 20)")] int maxResults = 20,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_semantic_search",
            format,
            ct => _semanticSearchAnalysisService.SearchAsync(
                query,
                projectFilter,
                includeTests,
                maxResults,
                ct),
            cancellationToken);
}
