using System.ComponentModel;
using CSharpLspMcp.Analysis.Lsp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools.Hierarchy;

[McpServerToolType]
public sealed class HierarchyTools : CSharpToolBase
{
    private readonly ILogger<HierarchyTools> _logger;
    private readonly CSharpHierarchyAnalysisService _hierarchyAnalysisService;

    public HierarchyTools(
        ILogger<HierarchyTools> logger,
        CSharpHierarchyAnalysisService hierarchyAnalysisService)
    {
        _logger = logger;
        _hierarchyAnalysisService = hierarchyAnalysisService;
    }

    [McpServerTool(Name = "csharp_find_implementations")]
    [Description("Find implementations of the symbol at the given position, including interface and override targets.")]
    public Task<string> FindImplementationsAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based character position")] int character,
        [Description("Content of the file (optional)")] string? content,
        [Description("Maximum number of results to return (default: 20)")] int maxResults = 20,
        CancellationToken cancellationToken = default)
        => ExecuteToolAsync(
            _logger,
            "csharp_find_implementations",
            ct => _hierarchyAnalysisService.FindImplementationsAsync(filePath, line, character, content, maxResults, ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_call_hierarchy")]
    [Description("Get incoming and outgoing calls for the symbol at the given position.")]
    public Task<string> GetCallHierarchyAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based character position")] int character,
        [Description("Content of the file (optional)")] string? content,
        [Description("Maximum number of incoming and outgoing results to show (default: 20)")] int maxResults = 20,
        CancellationToken cancellationToken = default)
        => ExecuteToolAsync(
            _logger,
            "csharp_call_hierarchy",
            ct => _hierarchyAnalysisService.GetCallHierarchyAsync(filePath, line, character, content, maxResults, ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_type_hierarchy")]
    [Description("Get immediate supertypes and subtypes for the type at the given position.")]
    public Task<string> GetTypeHierarchyAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based character position")] int character,
        [Description("Content of the file (optional)")] string? content,
        [Description("Maximum number of supertype and subtype results to show (default: 20)")] int maxResults = 20,
        CancellationToken cancellationToken = default)
        => ExecuteToolAsync(
            _logger,
            "csharp_type_hierarchy",
            ct => _hierarchyAnalysisService.GetTypeHierarchyAsync(filePath, line, character, content, maxResults, ct),
            cancellationToken);
}
