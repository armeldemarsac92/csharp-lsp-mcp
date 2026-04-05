using System.ComponentModel;
using CSharpLspMcp.Analysis.Lsp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools.Analysis;

[McpServerToolType]
public sealed class AnalysisTools : CSharpToolBase
{
    private readonly ILogger<AnalysisTools> _logger;
    private readonly CSharpSymbolAnalysisService _symbolAnalysisService;

    public AnalysisTools(
        ILogger<AnalysisTools> logger,
        CSharpSymbolAnalysisService symbolAnalysisService)
    {
        _logger = logger;
        _symbolAnalysisService = symbolAnalysisService;
    }

    [McpServerTool(Name = "csharp_analyze_symbol")]
    [Description("Build a one-shot symbol report: identity, location, hover, definitions, references, tests, implementations, callers, callees, and type hierarchy.")]
    public Task<string> AnalyzeSymbolAsync(
        [Description("Workspace symbol query. Leave empty to analyze by file position instead.")] string? symbolQuery = null,
        [Description("Absolute or workspace-relative path to the C# file. Required when analyzing by file position.")] string? filePath = null,
        [Description("0-based line number for the symbol position. Use with filePath and character.")] int line = -1,
        [Description("0-based character position for the symbol position. Use with filePath and line.")] int character = -1,
        [Description("Content of the file (optional, reads from disk when null or empty).")] string? content = null,
        [Description("Maximum number of references and hierarchy edges to include (default: 10)")] int maxResults = 10,
        CancellationToken cancellationToken = default)
        => ExecuteToolAsync(
            _logger,
            "csharp_analyze_symbol",
            ct => _symbolAnalysisService.AnalyzeSymbolAsync(
                symbolQuery,
                filePath,
                line,
                character,
                content,
                maxResults,
                ct),
            cancellationToken);
}
