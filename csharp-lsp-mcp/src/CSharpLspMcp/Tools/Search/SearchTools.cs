using System.ComponentModel;
using CSharpLspMcp.Analysis.Lsp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools.Search;

[McpServerToolType]
public sealed class SearchTools : CSharpToolBase
{
    private readonly ILogger<SearchTools> _logger;
    private readonly CSharpSearchAnalysisService _searchAnalysisService;

    public SearchTools(
        ILogger<SearchTools> logger,
        CSharpSearchAnalysisService searchAnalysisService)
    {
        _logger = logger;
        _searchAnalysisService = searchAnalysisService;
    }

    [McpServerTool(Name = "csharp_search_symbols")]
    [Description("Search symbols across the current workspace by name. Results include symbol kind, container, and source location.")]
    public Task<string> SearchSymbolsAsync(
        [Description("Search query for symbol name. Can be empty to inspect top-ranked workspace symbols.")] string query,
        [Description("Maximum number of results to return (default: 20)")] int maxResults = 20,
        CancellationToken cancellationToken = default)
        => ExecuteToolAsync(
            _logger,
            "csharp_search_symbols",
            ct => _searchAnalysisService.SearchSymbolsAsync(query, maxResults, ct),
            cancellationToken);
}
