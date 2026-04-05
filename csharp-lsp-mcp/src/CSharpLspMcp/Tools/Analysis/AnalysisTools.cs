using System.ComponentModel;
using CSharpLspMcp.Analysis.Lsp;
using CSharpLspMcp.Analysis.Quality;
using CSharpLspMcp.Analysis.Testing;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools.Analysis;

[McpServerToolType]
public sealed class AnalysisTools : CSharpToolBase
{
    private readonly ILogger<AnalysisTools> _logger;
    private readonly CSharpDeadCodeAnalysisService _deadCodeAnalysisService;
    private readonly CSharpSymbolAnalysisService _symbolAnalysisService;
    private readonly CSharpTestMapAnalysisService _testMapAnalysisService;

    public AnalysisTools(
        ILogger<AnalysisTools> logger,
        CSharpSymbolAnalysisService symbolAnalysisService,
        CSharpTestMapAnalysisService testMapAnalysisService,
        CSharpDeadCodeAnalysisService deadCodeAnalysisService)
    {
        _logger = logger;
        _symbolAnalysisService = symbolAnalysisService;
        _testMapAnalysisService = testMapAnalysisService;
        _deadCodeAnalysisService = deadCodeAnalysisService;
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
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_analyze_symbol",
            format,
            ct => _symbolAnalysisService.AnalyzeSymbolAsync(
                symbolQuery,
                filePath,
                line,
                character,
                content,
                maxResults,
                ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_test_map")]
    [Description("Map a production file or symbol name to likely related tests using file-name and content-reference heuristics.")]
    public Task<string> GetTestMapAsync(
        [Description("Absolute or workspace-relative path to the production C# file.")] string? filePath = null,
        [Description("Optional symbol name or fully qualified type/member name to map to tests.")] string? symbolQuery = null,
        [Description("Maximum number of related tests to return (default: 10)")] int maxResults = 10,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_test_map",
            format,
            ct => _testMapAnalysisService.GetTestMapAsync(
                filePath,
                symbolQuery,
                maxResults,
                ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_find_dead_code_candidates")]
    [Description("Find best-effort dead code candidates such as unused private methods, unused private fields, and unreferenced internal types.")]
    public Task<string> FindDeadCodeCandidatesAsync(
        [Description("Include unused private methods and fields (default: true)")] bool includePrivateMembers = true,
        [Description("Include unreferenced internal types (default: true)")] bool includeInternalTypes = true,
        [Description("Include candidates from test projects and test paths (default: false)")] bool includeTests = false,
        [Description("Maximum number of candidates to return (default: 20)")] int maxResults = 20,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_find_dead_code_candidates",
            format,
            ct => _deadCodeAnalysisService.FindDeadCodeCandidatesAsync(
                includePrivateMembers,
                includeInternalTypes,
                includeTests,
                maxResults,
                ct),
            cancellationToken);
}
